using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

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