using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace RoselineMCP.Services;

/// <summary>
/// Service for managing MSBuild registration and workspace creation.
/// </summary>
public class MSBuildService : IMSBuildService
{
    private readonly ILogger<MSBuildService> _logger;
    private static bool _msBuildRegistered = false;
    private static readonly object _msBuildLock = new();

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
            if (!_msBuildRegistered)
            {
                try
                {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                    if (instances.Length > 0)
                    {
                        MSBuildLocator.RegisterInstance(instances.First());
                        _msBuildRegistered = true;
                        _logger.LogInformation("MSBuild registered successfully");
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
    }

    /// <inheritdoc/>
    public MSBuildWorkspace CreateWorkspace()
    {
        EnsureMSBuildRegistered();
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (sender, e) =>
        {
            _logger.LogWarning("Workspace failed: {Message}", e.Diagnostic.Message);
        };

        return workspace;
    }
}