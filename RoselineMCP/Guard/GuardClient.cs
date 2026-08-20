using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RoselineMCP.Configuration;
using RoselineMCP.Services;

namespace RoselineMCP.Guard;

/// <summary>
/// The <c>roseline-mcp guard</c> verb: reads a <c>PostToolUse</c> hook envelope on stdin, asks the
/// running server whether the write that just happened introduced compiler errors, and reports them
/// to the agent through stderr.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exit 0 with no output is the answer to every uncertainty.</b> Wrong hook event, a path that is
/// not an absolute <c>.cs</c> file, no server listening, a server that does not answer in time, a
/// malformed reply, an empty verdict — all of them are silence. A guard that cannot inform must not
/// be able to interrupt, and infrastructure trouble is never the agent's problem to solve.
/// </para>
/// <para>
/// <b>Exit 2 is the only speaking path</b>, and it is what makes the harness surface stderr to the
/// agent as a system message inside the same turn. It is feedback, not prevention: <c>PostToolUse</c>
/// carries no blocking decision — the write already happened.
/// </para>
/// <para>
/// <b>stdout is never written.</b> The harness parses it as the hook's JSON result, so a stray byte
/// there is a malformed hook response rather than a message.
/// </para>
/// </remarks>
public static class GuardClient
{
    private const string PostToolUse = "PostToolUse";

    /// <summary>
    /// Runs the guard verb over the supplied streams.
    /// </summary>
    /// <param name="stdin">The hook envelope, as JSON.</param>
    /// <param name="stdout">Never written to; taken as a parameter so a test can prove that.</param>
    /// <param name="stderr">Where an introduced-errors report is written.</param>
    /// <param name="options">Guard configuration — endpoint address and the wait bound.</param>
    /// <param name="cancellationToken">Token used to cancel the round trip.</param>
    /// <returns><c>0</c> to stay silent, <c>2</c> to surface <paramref name="stderr"/> to the agent.</returns>
    public static async Task<int> RunAsync(
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        RoselineMcpOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(options);

        var filePath = ReadTargetPath(await stdin.ReadToEndAsync(cancellationToken));
        if (filePath is null)
        {
            return 0;
        }

        var report = await AskAsync(filePath, options, cancellationToken);
        if (string.IsNullOrWhiteSpace(report))
        {
            return 0;
        }

        await stderr.WriteLineAsync(report);
        return 2;
    }

    /// <summary>
    /// Extracts the file this run should ask about, or <see langword="null"/> when there is nothing
    /// to ask — which covers every malformed, irrelevant or unusable envelope.
    /// </summary>
    internal static string? ReadTargetPath(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        HookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HookEnvelope>(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null
            || !string.Equals(envelope.HookEventName, PostToolUse, StringComparison.Ordinal))
        {
            return null;
        }

        var filePath = envelope.ToolInput?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
        {
            // The envelope's `cwd` is the agent's, not the server's, so a relative path could only be
            // resolved against the wrong tree.
            return null;
        }

        // C# only for now: a .csproj or .props edit changes the build graph, which the guard's
        // text-forward baseline cannot express and which deserves its own decision.
        return Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? filePath
            : null;
    }

    private static async Task<string?> AskAsync(string filePath, RoselineMcpOptions options, CancellationToken cancellationToken)
    {
        var address = GuardEndpoint.ResolveAddress(options);

        using var deadline = options.GuardTimeout > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        deadline?.CancelAfter(options.GuardTimeout);
        var token = deadline?.Token ?? cancellationToken;

        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(address), token);

            await using var stream = new NetworkStream(client, ownsSocket: false);

            var request = JsonSerializer.Serialize(new GuardRequest { FilePath = filePath }) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), token);
            await stream.FlushAsync(token);

            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(token);
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<GuardResponse>(line, GuardJson.Options);
            return response is null || response.Silent ? null : response.Report;
        }
        catch (Exception ex) when (ex is SocketException
                                      or OperationCanceledException
                                      or IOException
                                      or JsonException
                                      or ObjectDisposedException
                                      or ArgumentException
                                      or NotSupportedException
                                      or PlatformNotSupportedException)
        {
            // No server, no socket file, a path too long for AF_UNIX, a timeout, a truncated or
            // malformed reply. None of these is the agent's problem, and none of them justifies
            // interrupting it. Nothing is logged either: the process's stderr IS the message channel.
            return null;
        }
    }
}
