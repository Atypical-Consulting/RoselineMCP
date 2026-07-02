using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Interface for applying automated code fixes to C# projects.
/// </summary>
public interface ICodeFixService
{
    /// <summary>
    /// Applies code fixes for specified diagnostic IDs in a project.
    /// </summary>
    /// <param name="project">Project name or path to .csproj file.</param>
    /// <param name="ids">List of diagnostic IDs to fix.</param>
    /// <param name="previewOnly">
    /// If true (the default), only preview changes without applying them. Defaults to
    /// <see langword="true"/> so that any caller which omits this parameter gets the
    /// non-destructive, read-only behavior — callers must opt in explicitly by passing
    /// <see langword="false"/> to write changes to disk.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>Response containing changed files, patch, and fix statistics.</returns>
    Task<ApplyFixesResponse> ApplyFixesAsync(
        string project,
        List<string> ids,
        bool previewOnly = true,
        CancellationToken cancellationToken = default);
}