using System.Net.Sockets;
using System.Text;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Guard;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="GuardClient"/> — the <c>roseline-mcp guard</c> verb, which is what the agent
/// harness actually executes after every file write.
/// </summary>
/// <remarks>
/// <para>
/// The contract under test is almost entirely about <b>not</b> speaking. Exit <c>0</c> with empty
/// output is the answer to every uncertainty: the wrong hook event, a path that is not an absolute
/// <c>.cs</c> file, no server listening, a server that does not answer in time, or a verdict with
/// nothing in it. Only one path returns <c>2</c>, and only that path writes to stderr.
/// </para>
/// <para>
/// <b>stdout must stay empty in every case.</b> The harness parses stdout as the hook's JSON result;
/// stray output there is a malformed hook response, not a message.
/// </para>
/// </remarks>
public class GuardClientTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = [];

    public GuardClientTests()
    {
        // Short: a Unix domain socket address is capped near 104 bytes.
        _root = Path.Combine(Path.GetTempPath(), $"rc{Guid.NewGuid():N}"[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch { /* ignored */ }
        }

        try { Directory.Delete(_root, true); } catch { /* ignored */ }

        GC.SuppressFinalize(this);
    }

    private string SocketPath => Path.Combine(_root, "g.sock");

    private RoselineMcpOptions Options(int timeoutMs = 5_000) =>
        new() { Guard = true, GuardEndpoint = SocketPath, GuardTimeout = timeoutMs };

    private string CsFile(string name = "Widget.cs")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "public class Widget { }");
        return path;
    }

    private static string Envelope(string? filePath, string hookEvent = "PostToolUse", string tool = "Edit")
    {
        var fileJson = filePath is null ? "null" : System.Text.Json.JsonSerializer.Serialize(filePath);
        return "{\"hook_event_name\":\"" + hookEvent + "\",\"tool_name\":\"" + tool
            + "\",\"cwd\":\"/somewhere/else\",\"tool_input\":{\"file_path\":" + fileJson + "}}";
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    private async Task<Run> RunAsync(string envelope, RoselineMcpOptions? options = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await GuardClient.RunAsync(
            new StringReader(envelope), stdout, stderr, options ?? Options(), TestContext.Current.CancellationToken);

        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Starts a real endpoint backed by a guard service that returns <paramref name="report"/>.</summary>
    private async Task StartEndpointAsync(GuardReport report)
    {
        var service = A.Fake<IGuardService>();
        A.CallTo(() => service.VerifyFileAsync(A<string>._, A<CancellationToken>._)).Returns(Task.FromResult(report));

        var endpoint = new GuardEndpoint(
            service,
            Microsoft.Extensions.Options.Options.Create(Options()),
            A.Fake<ILogger<GuardEndpoint>>());

        _disposables.Add(endpoint);
        await endpoint.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A listener that accepts and then never answers — the timeout case.</summary>
    private void StartBlackHole()
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(4);
        _disposables.Add(listener);

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var connection = await listener.AcceptAsync();
                    _disposables.Add(connection);   // held open, never written to
                }
            }
            catch
            {
                // listener disposed
            }
        });
    }

    // ---- the silent paths --------------------------------------------------------------------

    [Fact]
    public async Task A_Hook_Event_That_Is_Not_PostToolUse_Is_Ignored()
    {
        var run = await RunAsync(Envelope(CsFile(), hookEvent: "PreToolUse"));

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();
        run.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Missing_File_Path_Is_Ignored()
    {
        (await RunAsync(Envelope(null))).ExitCode.ShouldBe(0);
        (await RunAsync("""{"hook_event_name":"PostToolUse","tool_name":"Edit"}""")).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task A_Relative_File_Path_Is_Ignored()
    {
        // The envelope's `cwd` is the agent's, not the server's — a relative path could only be
        // resolved against the wrong tree, so the client refuses to send it.
        var run = await RunAsync(Envelope(Path.Combine("src", "Widget.cs")));

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Non_CSharp_File_Is_Ignored()
    {
        var readme = Path.Combine(_root, "README.md");
        File.WriteAllText(readme, "# hello");

        (await RunAsync(Envelope(readme))).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Malformed_Input_Is_Ignored()
    {
        (await RunAsync("this is not json")).ExitCode.ShouldBe(0);
        (await RunAsync("")).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task No_Server_Listening_Is_Ignored()
    {
        // Nothing was started, so the socket path does not exist. A guard that cannot inform must
        // not be able to interrupt: this is the single most important silent path.
        var run = await RunAsync(Envelope(CsFile()));

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();
        run.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Server_That_Never_Answers_Times_Out_Silently()
    {
        StartBlackHole();

        var run = await RunAsync(Envelope(CsFile()), Options(timeoutMs: 300));

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Silent_Verdict_Produces_No_Output()
    {
        await StartEndpointAsync(GuardReport.Quiet("/repo/App.sln"));

        var run = await RunAsync(Envelope(CsFile()));

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();
        run.Stdout.ShouldBeEmpty();
    }

    // ---- the one speaking path ---------------------------------------------------------------

    [Fact]
    public async Task Introduced_Errors_Exit_2_With_The_Report_On_Stderr()
    {
        var verdict = new VerificationVerdict { Compiles = false, ScopeComplete = true };
        await StartEndpointAsync(GuardReport.Speaking(verdict, "Widget.cs(3,9): CS0103: nope", "/repo/App.sln"));

        var run = await RunAsync(Envelope(CsFile()));

        // Exit 2 is what surfaces stderr to the agent as a system message in the same turn.
        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain("CS0103");

        // stdout is the hook's JSON result channel; the client never writes there.
        run.Stdout.ShouldBeEmpty();
    }
}
