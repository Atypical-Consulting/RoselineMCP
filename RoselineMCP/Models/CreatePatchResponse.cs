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