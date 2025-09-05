using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

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