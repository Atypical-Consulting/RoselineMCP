using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Base class for MCP protocol-level tests. Starts a fresh in-process MCP server + client pair
/// (<see cref="McpProtocolTestHost"/>) before each test method and tears it down afterward.
/// </summary>
/// <remarks>
/// <see cref="ISolutionAnalyzerService"/> and <see cref="ICodeFixService"/> are registered as
/// FakeItEasy fakes (exposed via <see cref="AnalyzerService"/>/<see cref="CodeFixService"/> so
/// individual tests can stub return values or exceptions) because their real implementations do
/// MSBuild workspace loading and, for Git URLs, network clones — neither of which these protocol
/// tests need or are allowed to depend on. <see cref="IPatchService"/>/<see cref="IDiffService"/>
/// are real, since they are pure, in-memory text diffing with no I/O.
/// </remarks>
public abstract class McpProtocolTestBase : IAsyncLifetime
{
    private McpProtocolTestHost? _host;

    protected ISolutionAnalyzerService AnalyzerService { get; } = A.Fake<ISolutionAnalyzerService>();

    protected ICodeFixService CodeFixService { get; } = A.Fake<ICodeFixService>();

    protected ICodeNavigationService NavigationService { get; } = A.Fake<ICodeNavigationService>();

    protected ICodeEditService EditService { get; } = A.Fake<ICodeEditService>();

    protected McpClient Client => _host?.Client
        ?? throw new InvalidOperationException($"{nameof(Client)} is only available once {nameof(InitializeAsync)} has completed.");

    public virtual async ValueTask InitializeAsync()
    {
        _host = await McpProtocolTestHost.StartAsync(services =>
        {
            services.AddSingleton(AnalyzerService);
            services.AddSingleton(CodeFixService);
            services.AddSingleton(NavigationService);
            services.AddSingleton(EditService);
            services.AddSingleton<IDiffService, DiffService>();
            services.AddSingleton<IPatchService, PatchService>();
        });
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }
}
