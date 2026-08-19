using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RoselineMCP.Configuration;
using RoselineMCP.Tools;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// In-process MCP protocol test harness.
/// </summary>
/// <remarks>
/// <para>
/// The ModelContextProtocol C# SDK (the exact <c>ModelContextProtocol</c> / <c>ModelContextProtocol.Core</c>
/// packages this project depends on) does not ship a separate test-support assembly, but it does ship a
/// reusable in-memory transport pair specifically documented for this purpose:
/// <c>ModelContextProtocol.Protocol.StreamClientTransport</c> ("useful for ... testing purposes. It works
/// with any readable and writable streams") on the client side, and the
/// <c>WithStreamServerTransport(Stream inputStream, Stream outputStream)</c> builder extension
/// (<c>Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions</c>) on the server side. Both are
/// built on top of the same <c>StreamServerTransport</c>/<c>StreamClientSessionTransport</c> newline-delimited
/// JSON-RPC framing used by the real stdio transport, so wiring them to a pair of in-memory duplex streams
/// (via <see cref="System.IO.Pipelines.Pipe"/>) drives the exact same JSON-RPC pipeline
/// (initialize handshake, tools/list, tools/call, MCP error envelopes) as the shipped
/// <c>roseline-mcp</c> executable, without spawning a real process or touching stdio.
/// </para>
/// <para>
/// This harness builds the server side the same way <c>Program.cs</c> does
/// (<c>AddMcpServer().WithToolsFromAssembly()</c>), so any tool discovered here is exactly what a real
/// client talking to the packaged tool would see. Only the transport (stream pair instead of stdio) and the
/// injected business services (fakes, so tests don't need network access, MSBuild, or a real solution on
/// disk) differ from the production host.
/// </para>
/// </remarks>
internal sealed class McpProtocolTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    public McpClient Client { get; }

    private McpProtocolTestHost(IHost host, McpClient client)
    {
        _host = host;
        Client = client;
    }

    /// <summary>
    /// Builds and starts an in-process MCP server (wired with the real
    /// <c>[McpServerTool]</c>-decorated tools from the RoselineMCP assembly) connected to a real
    /// <see cref="McpClient"/> over an in-memory duplex stream pair, and completes the MCP
    /// initialize handshake.
    /// </summary>
    /// <param name="configureServices">
    /// Callback used to register the business services (<see cref="RoselineMCP.Interfaces.ISolutionAnalyzerService"/>,
    /// <see cref="RoselineMCP.Interfaces.ICodeFixService"/>, <see cref="RoselineMCP.Interfaces.IPatchService"/>, etc.)
    /// the tools depend on. Tests typically register fakes here to avoid any real MSBuild/Git/filesystem work.
    /// </param>
    /// <param name="elicitationHandler">
    /// Optional handler for server-initiated <c>elicitation/create</c> requests. When supplied, the
    /// client advertises the elicitation capability and round-trips every elicitation to this
    /// handler; when omitted, the client advertises no elicitation support at all.
    /// </param>
    /// <param name="configureOptions">
    /// Optional callback to set the <see cref="RoselineMcpOptions"/> the tools see via
    /// <c>IOptions&lt;RoselineMcpOptions&gt;</c>. Omit it for production defaults.
    /// </param>
    public static async Task<McpProtocolTestHost> StartAsync(
        Action<IServiceCollection> configureServices,
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicitationHandler = null,
        Action<RoselineMcpOptions>? configureOptions = null)
    {
        // Two independent duplex pipes give us four unidirectional streams: the client writes to
        // clientToServer and the server reads from it, while the server writes to serverToClient and
        // the client reads from it. This mirrors how stdio hooks a child process's stdin/stdout together.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var hostBuilder = new HostBuilder();
        hostBuilder.ConfigureLogging(logging =>
        {
            // Keep test output clean; the transport/session already logs plenty at Trace/Debug.
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });
        hostBuilder.ConfigureServices(services =>
        {
            services
                // Mirror Program.cs's explicit serverInfo (real package semver instead of the
                // SDK's AssemblyVersion default) so the handshake tested here is the shipped one.
                .AddMcpServer(options => options.ServerInfo = RoselineServerInfo.Create())
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(AnalyzeSolutionTool).Assembly);

            // Mirrors Program.cs, which binds this so it's injectable via IOptions<RoselineMcpOptions> on
            // every tool method. Registering it here (even with defaults) keeps the resulting tool JSON
            // schema identical to production: without it, the DI container wouldn't recognize
            // IOptions<RoselineMcpOptions> as a known service type, and the SDK would try to bind it from
            // the caller-supplied arguments instead of excluding it from the schema.
            services.Configure<RoselineMcpOptions>(options => configureOptions?.Invoke(options));

            configureServices(services);
        });

        var host = hostBuilder.Build();
        await host.StartAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream());

        // When a test supplies an elicitation handler, advertise the client's elicitation capability
        // and wire the handler so the server's ElicitAsync round-trips to it (rather than failing the
        // capability check and falling back).
        McpClientOptions? clientOptions = elicitationHandler is null
            ? null
            : new McpClientOptions
            {
                Capabilities = new ClientCapabilities
                {
                    Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() },
                },
                Handlers = new McpClientHandlers { ElicitationHandler = elicitationHandler },
            };

        var client = await McpClient.CreateAsync(clientTransport, clientOptions);

        return new McpProtocolTestHost(host, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }
}
