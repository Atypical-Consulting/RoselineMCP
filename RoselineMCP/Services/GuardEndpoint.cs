using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Opt-in local endpoint that serves the compile-guard verdict to the <c>roseline-mcp guard</c> hook
/// client, over a Unix domain socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transport, not two.</b> <c>AF_UNIX</c> is supported on Windows too (since Windows 10
/// 1803), so the endpoint uses a filesystem-path socket everywhere rather than branching to named
/// pipes. Two transports would mean two connect paths, two lifetimes and two sets of failure modes
/// for a component whose entire contract is "fail silently" — the branch would be the least-tested
/// code in the feature. The one thing that genuinely differs is the permission model: the
/// <c>0600</c> mode bit below is Unix-only, and on Windows the socket file inherits the directory
/// ACL instead.
/// </para>
/// <para>
/// <b>Why an endpoint at all.</b> The guard has to answer within a hook, and the whole saving comes
/// from this process's already-warm <c>MSBuildWorkspace</c>. A hook that spawned its own server would
/// pay a cold MSBuild load per write, and a second long-lived process would hold a second copy of the
/// solution — which <c>docs/ARCHITECTURE.md</c> § Memory Management measured as the worst
/// configuration available. So the hook is a thin client and this is what it talks to.
/// </para>
/// <para>
/// <b>It is off unless asked for.</b> Nothing binds, and no socket file is created, when
/// <see cref="RoselineMcpOptions.Guard"/> is <see langword="false"/>. When it is on, the socket is a
/// per-user path with mode <c>0600</c>, it never listens on the network, and every failure is logged
/// to stderr — stdout belongs to the MCP JSON-RPC channel and writing there corrupts the protocol.
/// </para>
/// </remarks>
public sealed class GuardEndpoint : IHostedService, IDisposable
{
    private readonly IGuardService _guardService;
    private readonly RoselineMcpOptions _options;
    private readonly ILogger<GuardEndpoint> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private Socket? _listener;
    private Task? _acceptLoop;
    private string? _boundPath;
    private bool _disposed;

    /// <summary>Initializes a new <see cref="GuardEndpoint"/>.</summary>
    public GuardEndpoint(
        IGuardService guardService,
        IOptions<RoselineMcpOptions> options,
        ILogger<GuardEndpoint> logger)
    {
        _guardService = guardService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The path this endpoint actually bound, or <see langword="null"/> when it is not listening.</summary>
    public string? BoundPath => _boundPath;

    /// <summary>
    /// The endpoint address for a given configuration — the single definition shared by the server
    /// and the <c>guard</c> client, so the two can never disagree about where to meet.
    /// </summary>
    /// <remarks>
    /// Per-user by default: two accounts on one machine must not share a guard, and a world-writable
    /// path would let any local process ask this server to compile arbitrary directories. Kept short
    /// deliberately — a Unix domain socket path is capped near 104 bytes.
    /// </remarks>
    public static string ResolveAddress(RoselineMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.GuardEndpoint))
        {
            return options.GuardEndpoint;
        }

        var user = Sanitize(Environment.UserName);
        return Path.Combine(Path.GetTempPath(), $"roseline-{user}", "g.sock");
    }

    /// <summary>
    /// Creates the endpoint's parent directory owner-only, so the socket path cannot be squatted
    /// before the server binds it.
    /// </summary>
    /// <remarks>
    /// The <c>0600</c> mode on the socket itself protects it once bound — it does not stop another
    /// local account from creating the file first. On Linux with the default <c>/tmp</c> that is a
    /// real path: the sticky bit then blocks the victim's <c>File.Delete</c>, bind fails, and the
    /// victim's <em>client</em> connects to the squatter's socket instead. That matters more than a
    /// denial of service, because <c>GuardClient</c> writes the reply verbatim to stderr and the
    /// harness surfaces stderr to the agent — so the squatter would be injecting text into an
    /// agent's context. An owner-only parent directory closes the pre-creation window.
    /// </remarks>
    private static void PrepareEndpointDirectory(string address)
    {
        var directory = Path.GetDirectoryName(address);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (created)
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var sanitized = builder.ToString().Trim('-');
        return sanitized.Length == 0 ? "default" : sanitized;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Guard)
        {
            // Default-off: nothing binds, no socket file appears, nothing to attack.
            _logger.LogDebug("Compile guard endpoint disabled (RoselineMCP:Guard=false).");
            return Task.CompletedTask;
        }

        var address = ResolveAddress(_options);

        Socket? listener = null;

        try
        {
            PrepareEndpointDirectory(address);

            if (File.Exists(address))
            {
                if (IsLive(address))
                {
                    // Another RoselineMCP already owns this endpoint — the address is per-user, and a
                    // client spawns one server per project. Deleting it would silently kill that
                    // server's guard for the rest of its life. Stand down instead.
                    _logger.LogInformation(
                        "Compile guard endpoint {Address} is already served by another RoselineMCP process; this server will not open one.",
                        address);
                    return Task.CompletedTask;
                }

                // A leftover from a crashed process. Deleting it is required on EVERY platform:
                // Windows does not unlink an AF_UNIX socket file on close, so without this the next
                // bind fails with AddressAlreadyInUse forever.
                File.Delete(address);
            }

            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(address));
            listener.Listen(backlog: 8);

            if (!OperatingSystem.IsWindows())
            {
                // Owner-only. Without this the socket inherits the process umask, and anything local
                // could ask this server to load and compile a path of its choosing — which is
                // design-time MSBuild evaluation, i.e. code execution. See SECURITY.md. Windows has
                // no equivalent mode bit; the socket file inherits the directory ACL there.
                File.SetUnixFileMode(address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            _listener = listener;
            _boundPath = address;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);

            _logger.LogInformation("Compile guard endpoint listening at {Address}", address);
        }
        catch (Exception ex)
        {
            // Failing to open the guard must never take the MCP server down with it: the server's
            // job is the stdio protocol, and the guard is an accessory to it.
            if (!ReferenceEquals(listener, _listener))
            {
                listener?.Dispose();
            }

            _logger.LogError(ex, "Compile guard endpoint could not start at {Address}; the guard is unavailable.", address);
        }

        return Task.CompletedTask;
    }

    /// <summary>Whether something is actually listening on <paramref name="address"/> right now.</summary>
    private static bool IsLive(string address)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(address));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing accepting: a stale file, an ordinary file, or a path we cannot reach.
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener!.AcceptAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Compile guard endpoint stopped accepting.");
                return;
            }

            // One connection, one question, one answer — deliberately not multiplexed. Serving them
            // concurrently is safe because GuardService already joins concurrent requests for the
            // same solution.
            _ = Task.Run(() => ServeAsync(connection, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(Socket connection, CancellationToken cancellationToken)
    {
        try
        {
            using (connection)
            await using (var stream = new NetworkStream(connection, ownsSocket: false))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);

                var response = await HandleAsync(line, cancellationToken);

                var payload = JsonSerializer.Serialize(response, GuardJson.Options) + "\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Never to stdout: that is the MCP channel. And never rethrown — a guard that can crash
            // the server it lives in is worse than no guard.
            _logger.LogWarning(ex, "Compile guard request failed.");
        }
    }

    private async Task<GuardResponse> HandleAsync(string? line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return GuardResponse.Quiet();
        }

        GuardRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<GuardRequest>(line, GuardJson.Options);
        }
        catch (JsonException ex)
        {
            // Malformed input is answered with silence, not an error: the client's contract is that
            // anything other than a real verdict means "say nothing".
            _logger.LogDebug(ex, "Compile guard received a malformed request.");
            return GuardResponse.Quiet();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.FilePath))
        {
            return GuardResponse.Quiet();
        }

        try
        {
            var report = await _guardService.VerifyFileAsync(request.FilePath, cancellationToken);
            return report.Silent
                ? GuardResponse.Quiet()
                : new GuardResponse { Silent = false, Report = report.Text, ResolvedPath = report.ResolvedPath };
        }
        catch (ArgumentException ex)
        {
            // A relative or blank path. The client should not have sent it; answering silently keeps
            // the guard unable to interrupt an agent over its own plumbing.
            _logger.LogDebug(ex, "Compile guard rejected a request path.");
            return GuardResponse.Quiet();
        }
        catch (OperationCanceledException)
        {
            return GuardResponse.Quiet();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        _listener?.Dispose();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Shutdown is best-effort; a stuck accept must not hold the host open.
            }
        }

        RemoveSocketFile();
    }

    private void RemoveSocketFile()
    {
        // Only a path THIS instance bound is removed — _boundPath stays null when StartAsync stood
        // down because another server already owned the address, so shutting down here can never
        // delete a socket that is still serving somebody. Not Unix-only: Windows does not unlink an
        // AF_UNIX socket file on close, so skipping this there leaves a file that blocks every
        // subsequent bind.
        if (_boundPath is null)
        {
            return;
        }

        try
        {
            File.Delete(_boundPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a leftover file is cleared on the next start.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Dispose();
        _listener?.Dispose();
        RemoveSocketFile();
    }
}

/// <summary>The one question the guard endpoint answers.</summary>
public sealed class GuardRequest
{
    /// <summary>Absolute path of the file that was just written.</summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }
}

/// <summary>The guard endpoint's answer — almost always silence.</summary>
public sealed class GuardResponse
{
    /// <summary>Whether the guard has nothing to say.</summary>
    [JsonPropertyName("silent")]
    public bool Silent { get; set; }

    /// <summary>The rendered report, when the guard spoke.</summary>
    [JsonPropertyName("report")]
    public string? Report { get; set; }

    /// <summary>The <c>.sln</c>/<c>.csproj</c> the verdict is about, when one was resolved.</summary>
    [JsonPropertyName("resolvedPath")]
    public string? ResolvedPath { get; set; }

    /// <summary>The shared "nothing to say" answer.</summary>
    public static GuardResponse Quiet() => new() { Silent = true };
}

/// <summary>Serializer settings shared by the endpoint and its client.</summary>
internal static class GuardJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
