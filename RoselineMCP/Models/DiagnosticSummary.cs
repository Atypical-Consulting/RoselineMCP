using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

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