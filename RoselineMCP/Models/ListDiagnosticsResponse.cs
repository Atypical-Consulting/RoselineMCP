using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

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