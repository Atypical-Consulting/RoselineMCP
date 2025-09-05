using Microsoft.CodeAnalysis;

namespace RoselineMCP.Services;

/// <summary>
/// Service for filtering diagnostics based on various criteria.
/// </summary>
public class DiagnosticFilterService : IDiagnosticFilterService
{
    private readonly HashSet<string> _fixableDiagnosticIds;

    public DiagnosticFilterService()
    {
        _fixableDiagnosticIds = InitializeFixableDiagnosticIds();
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

        var requestedSeverity = Enum.Parse<DiagnosticSeverity>(severityFilter, true);
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
        return _fixableDiagnosticIds.Contains(id);
    }

    private static HashSet<string> InitializeFixableDiagnosticIds()
    {
        return new HashSet<string>
        {
            // Roslynator
            "RCS1001", "RCS1003", "RCS1018", "RCS1036", "RCS1037", "RCS1058", "RCS1059", "RCS1060",
            "RCS1213", "RCS1214", "RCS1215", "RCS1216", "RCS1217", "RCS1218", "RCS1220", "RCS1221",

            // StyleCop
            "SA1000", "SA1001", "SA1002", "SA1003", "SA1004", "SA1005", "SA1006", "SA1007", "SA1008",
            "SA1101", "SA1200", "SA1210",

            // Common C# compiler warnings
            "CS0168", "CS0219", "CS0414", "CS0649", "CS1591", "CS8019",

            // IDE suggestions
            "IDE0001", "IDE0002", "IDE0003", "IDE0004", "IDE0005", "IDE0007", "IDE0008", "IDE0009",
            "IDE0017", "IDE0028", "IDE0031", "IDE0041", "IDE0051", "IDE0052", "IDE0055", "IDE0060",
            "IDE0090", "IDE0100", "IDE0130", "IDE0160", "IDE0161", "IDE0200", "IDE0230", "IDE0250",
            "IDE1005", "IDE1006"
        };
    }
}