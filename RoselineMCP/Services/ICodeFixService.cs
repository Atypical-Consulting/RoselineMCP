using RoselineMCP.Models;

namespace RoselineMCP.Services;

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
    /// <param name="previewOnly">If true, only preview changes without applying them.</param>
    /// <returns>Response containing changed files, patch, and fix statistics.</returns>
    Task<ApplyFixesResponse> ApplyFixesAsync(
        string project,
        List<string> ids,
        bool previewOnly = false);
}