namespace RoselineMCP.Services;

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
}