using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="GuardEndpoint"/> — the opt-in local socket the <c>guard</c> hook client
/// talks to.
/// </summary>
/// <remarks>
/// Two properties matter more than the happy path. First, <b>nothing listens unless asked</b>: a
/// default install must not create a socket at all. Second, <b>every failure is silence</b> — a
/// malformed request, an unknown path, a throwing service. The guard sits in an agent's inner loop,
/// so anything it cannot answer confidently it must decline to answer at all, and it must never
/// write to stdout, which is the MCP JSON-RPC channel.
/// </remarks>
public class GuardEndpointTests : IDisposable
{
    private readonly string _root;

    public GuardEndpointTests()
    {
        // Short path on purpose: a Unix domain socket address is capped near 104 bytes, and macOS
        // temp directories are already long.
        _root = Path.Combine(Path.GetTempPath(), $"rg{Guid.NewGuid():N}"[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignored */ }

        GC.SuppressFinalize(this);
    }

    private string SocketPath => Path.Combine(_root, "g.sock");

    private GuardEndpoint CreateEndpoint(IGuardService service, bool enabled = true) =>
        new(
            service,
            Options.Create(new RoselineMcpOptions { Guard = enabled, GuardEndpoint = SocketPath }),
            A.Fake<ILogger<GuardEndpoint>>());

    /// <summary>Sends one raw line and reads one raw line back — the whole wire protocol.</summary>
    private async Task<string?> RoundTripAsync(string requestLine)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath));

        await using var stream = new NetworkStream(client, ownsSocket: false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(requestLine + "\n"), TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync(TestContext.Current.CancellationToken);
    }

    private static GuardResponse Parse(string? line)
    {
        line.ShouldNotBeNull();
        return JsonSerializer.Deserialize<GuardResponse>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static IGuardService ServiceReturning(GuardReport report)
    {
        var service = A.Fake<IGuardService>();
        A.CallTo(() => service.VerifyFileAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(report));
        return service;
    }

    [Fact]
    public async Task Nothing_Listens_When_The_Guard_Is_Disabled()
    {
        var endpoint = CreateEndpoint(ServiceReturning(GuardReport.Quiet()), enabled: false);
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);

        // Not merely "refuses connections" — the socket file must not exist at all.
        endpoint.BoundPath.ShouldBeNull();
        File.Exists(SocketPath).ShouldBeFalse();

        await endpoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_Client_Round_Trips_A_Verdict()
    {
        var verdict = new VerificationVerdict { Compiles = false, ScopeComplete = true };
        var endpoint = CreateEndpoint(ServiceReturning(
            GuardReport.Speaking(verdict, "two errors here", "/repo/App.sln")));
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);
        endpoint.BoundPath.ShouldBe(SocketPath);

        var response = Parse(await RoundTripAsync("""{"filePath":"/repo/App/Widget.cs"}"""));

        response.Silent.ShouldBeFalse();
        response.Report.ShouldBe("two errors here");
        response.ResolvedPath.ShouldBe("/repo/App.sln");

        await endpoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_Path_Outside_Every_Loaded_Solution_Answers_Silent()
    {
        var endpoint = CreateEndpoint(ServiceReturning(GuardReport.Quiet()));
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);

        var response = Parse(await RoundTripAsync("""{"filePath":"/nowhere/notes.cs"}"""));

        response.Silent.ShouldBeTrue();
        response.Report.ShouldBeNull();

        await endpoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_Malformed_Request_Answers_Silent_Instead_Of_Throwing()
    {
        var endpoint = CreateEndpoint(ServiceReturning(GuardReport.Quiet()));
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);

        Parse(await RoundTripAsync("this is not json at all")).Silent.ShouldBeTrue();
        Parse(await RoundTripAsync("{}")).Silent.ShouldBeTrue();
        Parse(await RoundTripAsync("""{"filePath":""}""")).Silent.ShouldBeTrue();

        await endpoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_Throwing_Guard_Service_Answers_Silent()
    {
        var service = A.Fake<IGuardService>();
        A.CallTo(() => service.VerifyFileAsync(A<string>._, A<CancellationToken>._))
            .Throws(new ArgumentException("relative path"));

        var endpoint = CreateEndpoint(service);
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);

        Parse(await RoundTripAsync("""{"filePath":"relative/Widget.cs"}""")).Silent.ShouldBeTrue();

        await endpoint.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_Endpoint_Never_Writes_To_Stdout()
    {
        // stdout is the MCP JSON-RPC channel; a single stray byte there corrupts the protocol for
        // the whole session, and the guard is the newest thing sharing this process.
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);

        try
        {
            var service = A.Fake<IGuardService>();
            A.CallTo(() => service.VerifyFileAsync(A<string>._, A<CancellationToken>._))
                .Throws(new InvalidOperationException("boom"));

            var endpoint = CreateEndpoint(service);
            using var _ = endpoint;

            await endpoint.StartAsync(TestContext.Current.CancellationToken);
            await RoundTripAsync("""{"filePath":"/repo/App/Widget.cs"}""");
            await RoundTripAsync("garbage");
            await endpoint.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(original);
        }

        captured.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_Removes_The_Socket_File()
    {
        var endpoint = CreateEndpoint(ServiceReturning(GuardReport.Quiet()));
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);
        File.Exists(SocketPath).ShouldBeTrue();

        await endpoint.StopAsync(TestContext.Current.CancellationToken);

        // A leftover socket makes the next bind() fail with AddressAlreadyInUse.
        File.Exists(SocketPath).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Stale_Socket_File_Does_Not_Prevent_Binding()
    {
        File.WriteAllText(SocketPath, "leftover from a crashed process");

        var endpoint = CreateEndpoint(ServiceReturning(GuardReport.Quiet()));
        using var _ = endpoint;

        await endpoint.StartAsync(TestContext.Current.CancellationToken);

        endpoint.BoundPath.ShouldBe(SocketPath);
        Parse(await RoundTripAsync("""{"filePath":"/repo/App/Widget.cs"}""")).Silent.ShouldBeTrue();

        await endpoint.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A second server must not steal an endpoint a first one is still serving. The address is
    /// per-user while a client spawns one server per project, so this is the ordinary case, not an
    /// exotic one — and stealing it would kill the first server's guard silently for the rest of its
    /// life, with clients getting a connection error that the contract turns into silence.
    /// </summary>
    [Fact]
    public async Task A_Second_Endpoint_Stands_Down_Rather_Than_Stealing_A_Live_Socket()
    {
        var first = CreateEndpoint(ServiceReturning(
            GuardReport.Speaking(new VerificationVerdict { ScopeComplete = true }, "from the first", "/repo/App.sln")));
        using var _ = first;
        await first.StartAsync(TestContext.Current.CancellationToken);
        first.BoundPath.ShouldBe(SocketPath);

        var second = CreateEndpoint(ServiceReturning(GuardReport.Quiet()));
        using var __ = second;
        await second.StartAsync(TestContext.Current.CancellationToken);

        second.BoundPath.ShouldBeNull();

        // The first server is still the one answering.
        Parse(await RoundTripAsync("""{"filePath":"/repo/App/Widget.cs"}""")).Report.ShouldBe("from the first");

        // ...and the stood-down server's shutdown must not remove the socket it never bound.
        await second.StopAsync(TestContext.Current.CancellationToken);
        File.Exists(SocketPath).ShouldBeTrue();
        Parse(await RoundTripAsync("""{"filePath":"/repo/App/Widget.cs"}""")).Report.ShouldBe("from the first");

        await first.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ResolveAddress_Prefers_The_Configured_Path_And_Is_Per_User_Otherwise()
    {
        GuardEndpoint.ResolveAddress(new RoselineMcpOptions { GuardEndpoint = "/tmp/explicit.sock" })
            .ShouldBe("/tmp/explicit.sock");

        var derived = GuardEndpoint.ResolveAddress(new RoselineMcpOptions());

        // Per-user, and inside a directory of its own: the socket's 0600 mode protects it once
        // bound, but only an owner-only PARENT closes the pre-creation squat window.
        derived.ShouldEndWith(".sock");
        Path.GetDirectoryName(derived).ShouldNotBe(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        Path.GetFileName(Path.GetDirectoryName(derived)!).ShouldStartWith("roseline-");

        // A Unix domain socket address is capped near 104 bytes on macOS; a derived path that
        // overran it would fail to bind on exactly the machines this is developed on.
        Encoding.UTF8.GetByteCount(derived).ShouldBeLessThan(104);
    }
}
