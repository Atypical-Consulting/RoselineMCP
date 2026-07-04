using ModelContextProtocol;
using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Interface for analyzing C# solutions and projects for diagnostics.
/// </summary>
public interface ISolutionAnalyzerService
{
    /// <summary>
    /// Analyzes a C# solution for diagnostics with filtering options.
    /// </summary>
    /// <param name="pathOrGit">Path to solution file, directory containing .sln file, or Git repository URL.</param>
    /// <param name="branch">Git branch name (only used if pathOrGit is a Git URL).</param>
    /// <param name="includePattern">Include pattern for project names.</param>
    /// <param name="excludePattern">Exclude pattern for project names.</param>
    /// <param name="severity">Minimum severity level to include.</param>
    /// <param name="maxDiagnostics">Maximum number of diagnostics to return.</param>
    /// <param name="progress">Optional sink for progress updates (clone/load/per-project analysis).</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>Analysis response containing diagnostics summary and details.</returns>
    Task<AnalyzeSolutionResponse> AnalyzeSolutionAsync(
        string pathOrGit,
        string? branch = null,
        string? includePattern = null,
        string? excludePattern = null,
        string? severity = null,
        int maxDiagnostics = 100,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists detailed diagnostics for a specific project. Loaded via <see cref="IProjectLoader"/>,
    /// so the same references the navigation/edit tools accept work here too.
    /// </summary>
    /// <param name="project">
    /// Project name, directory, <c>.csproj</c> path, or <c>.sln</c> path. When <see langword="null"/>
    /// or whitespace, the solution/project is auto-discovered from the working directory.
    /// </param>
    /// <param name="ids">Optional list of diagnostic IDs to filter.</param>
    /// <param name="files">Optional list of file patterns to filter.</param>
    /// <param name="max">Maximum number of diagnostic details to return.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>Response containing diagnostics list and statistics.</returns>
    Task<ListDiagnosticsResponse> ListDiagnosticsAsync(
        string? project,
        List<string>? ids = null,
        List<string>? files = null,
        int max = 100,
        CancellationToken cancellationToken = default);
}