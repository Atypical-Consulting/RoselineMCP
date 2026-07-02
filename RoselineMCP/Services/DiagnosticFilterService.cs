using Microsoft.CodeAnalysis;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Service for filtering diagnostics based on various criteria.
/// </summary>
public class DiagnosticFilterService : IDiagnosticFilterService
{
    private readonly ICodeFixProviderFactory _codeFixProviderFactory;

    /// <summary>
    /// Initializes a new instance of the DiagnosticFilterService.
    /// </summary>
    /// <param name="codeFixProviderFactory">
    /// Factory whose dynamically-loaded code fix providers are the single source of truth
    /// for which diagnostic IDs are actually fixable in this deployment.
    /// </param>
    public DiagnosticFilterService(ICodeFixProviderFactory codeFixProviderFactory)
    {
        _codeFixProviderFactory = codeFixProviderFactory;
    }

    /// <inheritdoc/>
    public bool ShouldAnalyzeProject(string projectName, string? includePattern, string? excludePattern)
    {
        if (!string.IsNullOrEmpty(excludePattern) && projectName.Contains(excludePattern))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(includePattern) && !projectName.Contains(includePattern))
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public bool ShouldIncludeDiagnostic(Diagnostic diagnostic, string? severityFilter)
    {
        if (diagnostic.IsSuppressed)
        {
            return false;
        }

        if (string.IsNullOrEmpty(severityFilter))
        {
            return true;
        }

        if (!Enum.TryParse<DiagnosticSeverity>(severityFilter, true, out var requestedSeverity))
        {
            // If invalid severity provided, include all diagnostics
            return true;
        }
        
        return diagnostic.Severity >= requestedSeverity;
    }

    /// <inheritdoc/>
    public bool FilterByIds(Diagnostic diagnostic, List<string>? ids)
    {
        if (ids == null || ids.Count == 0)
            return true;

        return ids.Contains(diagnostic.Id);
    }

    /// <inheritdoc/>
    public bool FilterByFiles(Diagnostic diagnostic, List<string>? files)
    {
        if (files == null || files.Count == 0)
            return true;

        var location = diagnostic.Location.GetLineSpan();
        if (location.Path == null)
            return false;

        return files.Any(f => location.Path.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public int GetSeverityPriority(string severity)
    {
        return severity.ToLower() switch
        {
            "error" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0
        };
    }

    /// <inheritdoc/>
    public bool IsFixableDiagnostic(string id)
    {
        // Single source of truth: whatever ICodeFixProviderFactory actually discovered from the
        // dynamically-loaded Roslyn/Roslynator code fix providers at runtime. Previously this
        // checked a hand-maintained static list that could silently drift out of sync with what
        // ApplyFixes could really fix (missing newly-available fixers, or claiming fixability for
        // an ID whose provider assembly failed to load).
        return _codeFixProviderFactory.GetFixableDiagnosticIds().Contains(id);
    }
}