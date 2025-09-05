using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for patch creation operations.
/// </summary>
public class CreatePatchResponse
{
    /// <summary>
    /// The generated unified diff patch.
    /// </summary>
    [JsonPropertyName("patch")]
    public string Patch { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether any changes were detected.
    /// </summary>
    [JsonPropertyName("hasChanges")]
    public bool HasChanges { get; set; }

    /// <summary>
    /// Number of lines added in the patch.
    /// </summary>
    [JsonPropertyName("linesAdded")]
    public int LinesAdded { get; set; }

    /// <summary>
    /// Number of lines removed in the patch.
    /// </summary>
    [JsonPropertyName("linesRemoved")]
    public int LinesRemoved { get; set; }

    /// <summary>
    /// Name of the file being patched.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "file.txt";

    /// <summary>
    /// Human-readable summary of the changes.
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Response model for code fix application operations.
/// </summary>
public class ApplyFixesResponse
{
    /// <summary>
    /// Name of the project where fixes were applied.
    /// </summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// List of diagnostic IDs that were successfully fixed.
    /// </summary>
    [JsonPropertyName("fixersApplied")]
    public List<string> FixersApplied { get; set; } = new();

    /// <summary>
    /// List of file paths that were modified.
    /// </summary>
    [JsonPropertyName("changedFiles")]
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>
    /// Unified diff patch showing all changes.
    /// </summary>
    [JsonPropertyName("patch")]
    public string Patch { get; set; } = string.Empty;

    /// <summary>
    /// Additional notes or warnings about the fix operation.
    /// </summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// Total number of fixes applied.
    /// </summary>
    [JsonPropertyName("fixedCount")]
    public int FixedCount { get; set; }

    /// <summary>
    /// Indicates whether this was a preview-only operation.
    /// </summary>
    [JsonPropertyName("previewOnly")]
    public bool PreviewOnly { get; set; }
}

/// <summary>
/// Response model for listing project diagnostics.
/// </summary>
public class ListDiagnosticsResponse
{
    /// <summary>
    /// Name of the analyzed project.
    /// </summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Total number of diagnostics found.
    /// </summary>
    [JsonPropertyName("totalDiagnostics")]
    public int TotalDiagnostics { get; set; }

    /// <summary>
    /// List of detailed diagnostic information.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public List<DiagnosticDetail> Diagnostics { get; set; } = new();

    /// <summary>
    /// Statistical breakdown of diagnostics.
    /// </summary>
    [JsonPropertyName("stats")]
    public DiagnosticStats Stats { get; set; } = new();

    /// <summary>
    /// List of diagnostic IDs that can be automatically fixed.
    /// </summary>
    [JsonPropertyName("suggestedFixableIds")]
    public List<string> SuggestedFixableIds { get; set; } = new();
}

/// <summary>
/// Statistical information about diagnostics.
/// </summary>
public class DiagnosticStats
{
    /// <summary>
    /// Count of diagnostics grouped by diagnostic ID.
    /// </summary>
    [JsonPropertyName("byId")]
    public Dictionary<string, int> ById { get; set; } = new();

    /// <summary>
    /// Count of diagnostics grouped by severity level.
    /// </summary>
    [JsonPropertyName("bySeverity")]
    public Dictionary<string, int> BySeverity { get; set; } = new();
}

/// <summary>
/// Response model for solution analysis operations.
/// </summary>
public class AnalyzeSolutionResponse
{
    /// <summary>
    /// Name of the solution file.
    /// </summary>
    [JsonPropertyName("solution")]
    public string Solution { get; set; } = string.Empty;

    /// <summary>
    /// Total number of projects in the solution.
    /// </summary>
    [JsonPropertyName("projects")]
    public int Projects { get; set; }

    /// <summary>
    /// Summary of diagnostics by severity level.
    /// </summary>
    [JsonPropertyName("diagnosticSummary")]
    public DiagnosticSummary DiagnosticSummary { get; set; } = new();

    /// <summary>
    /// List of the most important diagnostics found.
    /// </summary>
    [JsonPropertyName("topDiagnostics")]
    public List<DiagnosticDetail> TopDiagnostics { get; set; } = new();
}

/// <summary>
/// Summary of diagnostic counts by severity level.
/// </summary>
public class DiagnosticSummary
{
    /// <summary>
    /// Number of error-level diagnostics.
    /// </summary>
    [JsonPropertyName("error")]
    public int Error { get; set; }

    /// <summary>
    /// Number of warning-level diagnostics.
    /// </summary>
    [JsonPropertyName("warning")]
    public int Warning { get; set; }

    /// <summary>
    /// Number of info-level diagnostics.
    /// </summary>
    [JsonPropertyName("info")]
    public int Info { get; set; }

    /// <summary>
    /// Number of hidden-level diagnostics.
    /// </summary>
    [JsonPropertyName("hidden")]
    public int Hidden { get; set; }
}

/// <summary>
/// Detailed information about a specific diagnostic.
/// </summary>
public class DiagnosticDetail
{
    /// <summary>
    /// Name of the project containing the diagnostic.
    /// </summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// File path where the diagnostic was found.
    /// </summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// Line number of the diagnostic (1-based).
    /// </summary>
    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>
    /// Column number of the diagnostic (1-based).
    /// </summary>
    [JsonPropertyName("column")]
    public int Column { get; set; }

    /// <summary>
    /// Diagnostic ID (e.g., CS0168, IDE0005).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Severity level of the diagnostic.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// Descriptive message for the diagnostic.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}