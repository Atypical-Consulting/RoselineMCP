using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

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