namespace RoselineMCP.Interfaces;

/// <summary>
/// Service for managing MSBuild registration and workspace creation.
/// </summary>
public interface IMSBuildService
{
    /// <summary>
    /// Ensures MSBuild is registered for the current process.
    /// </summary>
    void EnsureMSBuildRegistered();

    /// <summary>
    /// Creates a new MSBuild workspace.
    /// </summary>
    /// <returns>A configured MSBuild workspace.</returns>
    Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace CreateWorkspace();

    /// <summary>
    /// <see langword="true"/> once a workspace has reported the SDK's workload resolver failing
    /// (a broken workload install, MSB4242). From then on every workspace is created with
    /// <c>MSBuildEnableWorkloadResolver=false</c>; a load that saw it flip should load again.
    /// </summary>
    bool WorkloadResolverDisabled { get; }
}