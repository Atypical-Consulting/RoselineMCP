using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Service for managing MSBuild registration and workspace creation.
/// </summary>
public class MSBuildService : IMSBuildService
{
    private readonly ILogger<MSBuildService> _logger;
    private static bool _msBuildRegistered;
    private static readonly Lock _msBuildLock = new();
    private volatile bool _workloadResolverDisabled;

    /// <summary>
    /// The global property that switches the SDK's workload imports off — the same switch as the
    /// <c>MSBuildEnableWorkloadResolver=false</c> environment variable <c>dotnet build</c> users
    /// set to get past MSB4242.
    /// </summary>
    internal const string WorkloadResolverProperty = "MSBuildEnableWorkloadResolver";

    /// <summary>
    /// Initializes a new instance of the MSBuildService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public MSBuildService(ILogger<MSBuildService> logger)
    {
        _logger = logger;
        EnsureMSBuildRegistered();
    }

    /// <inheritdoc/>
    public void EnsureMSBuildRegistered()
    {
        lock (_msBuildLock)
        {
            if (_msBuildRegistered)
            {
                return;
            }

            // Someone else in the process may already have registered MSBuild (RegisterInstance
            // throws in that case) — treat that as success rather than a failure to limp past.
            if (MSBuildLocator.IsRegistered)
            {
                _msBuildRegistered = true;
                return;
            }

            try
            {
                // Register the newest SDK, not whatever the locator happens to enumerate first —
                // older SDKs may not be able to load projects targeting newer frameworks.
                var instance = SelectPreferredInstance(
                    MSBuildLocator.QueryVisualStudioInstances().ToArray(),
                    i => i.Version);
                if (instance is not null)
                {
                    MSBuildLocator.RegisterInstance(instance);
                    _msBuildRegistered = true;
                    _logger.LogInformation(
                        "MSBuild registered: {Name} {Version} ({Path})",
                        instance.Name,
                        instance.Version,
                        instance.MSBuildPath);
                }
                else
                {
                    _logger.LogWarning("No MSBuild instances found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register MSBuild");
            }
        }
    }

    /// <summary>
    /// Picks the instance with the highest version. Generic so the selection policy is unit
    /// testable — <see cref="VisualStudioInstance"/> has an internal constructor and cannot be
    /// faked in tests.
    /// </summary>
    internal static T? SelectPreferredInstance<T>(IReadOnlyList<T> instances, Func<T, Version> getVersion)
        where T : class
        => instances.OrderByDescending(getVersion).FirstOrDefault();

    /// <inheritdoc/>
    public bool WorkloadResolverDisabled => _workloadResolverDisabled;

    /// <summary>
    /// Whether a <c>WorkspaceFailed</c> message is the SDK's workload resolver failing — what a
    /// workload set with missing or corrupt manifests does to <b>every</b> SDK-style project
    /// (MSB4242: "Workload set version … has missing manifests … Run dotnet workload repair").
    /// </summary>
    internal static bool IsWorkloadResolverFailure(string message)
        => message.Contains("MSBuildWorkloadSdkResolver", StringComparison.Ordinal);

    /// <inheritdoc/>
    public MSBuildWorkspace CreateWorkspace()
    {
        EnsureMSBuildRegistered();

        // Without a registered MSBuild instance, MSBuildWorkspace.Create() succeeds but every
        // subsequent load fails with a confusing assembly-resolution error. Fail fast with an
        // actionable message instead.
        if (!_msBuildRegistered)
        {
            throw new InvalidOperationException(
                "No MSBuild/.NET SDK instance could be registered — install the .NET SDK or check that 'dotnet' is on PATH. See earlier log entries for the underlying registration error.");
        }

        // A broken workload install fails the workload SDK resolver for every project, which
        // MSBuildWorkspace reports through WorkspaceFailed and then answers with projects that have
        // no documents — every symbol lookup comes back empty or not-found. Once seen, every later
        // workspace loads with the workload imports off; callers reload (see ProjectLoader).
        var workspace = _workloadResolverDisabled
            ? MSBuildWorkspace.Create(new Dictionary<string, string> { [WorkloadResolverProperty] = "false" })
            : MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (sender, e) =>
        {
            _logger.LogWarning("Workspace failed: {Message}", e.Diagnostic.Message);
            if (!_workloadResolverDisabled && IsWorkloadResolverFailure(e.Diagnostic.Message))
            {
                _workloadResolverDisabled = true;
                _logger.LogWarning(
                    "The .NET SDK workload resolver failed; loading with {Property}=false from now on. Run 'dotnet workload repair' to fix the SDK install.",
                    WorkloadResolverProperty);
            }
        };

        return workspace;
    }
}
