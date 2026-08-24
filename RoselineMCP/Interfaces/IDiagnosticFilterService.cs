using Microsoft.CodeAnalysis;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Service for filtering diagnostics based on various criteria.
/// </summary>
public interface IDiagnosticFilterService
{
    /// <summary>
    /// Determines if a project should be analyzed based on include/exclude patterns.
    /// </summary>
    bool ShouldAnalyzeProject(string projectName, string? includePattern, string? excludePattern);

    /// <summary>
    /// Determines if a diagnostic should be included based on severity filter.
    /// </summary>
    bool ShouldIncludeDiagnostic(Diagnostic diagnostic, string? severityFilter);

    /// <summary>
    /// Filters diagnostics by diagnostic IDs.
    /// </summary>
    bool FilterByIds(Diagnostic diagnostic, List<string>? ids);

    /// <summary>
    /// Filters diagnostics by file patterns.
    /// </summary>
    bool FilterByFiles(Diagnostic diagnostic, List<string>? files);

    /// <summary>
    /// Gets the priority value for a severity level.
    /// </summary>
    int GetSeverityPriority(string severity);

    /// <summary>
    /// Determines if a diagnostic ID is fixable by the process-wide providers (the Roslyn built-ins
    /// and the bundled catalog). Equivalent to <see cref="IsFixableDiagnostic(string, Project?)"/>
    /// with a <see langword="null"/> project.
    /// </summary>
    bool IsFixableDiagnostic(string id);

    /// <summary>
    /// Determines if a diagnostic ID is fixable — by the process-wide providers, or by a provider
    /// carried by <paramref name="project"/>'s own analyzer references.
    /// </summary>
    /// <param name="id">The diagnostic ID.</param>
    /// <param name="project">
    /// The target project whose analyzer references may carry the fixer; <see langword="null"/>
    /// consults the process-wide providers only.
    /// </param>
    bool IsFixableDiagnostic(string id, Project? project);
}
