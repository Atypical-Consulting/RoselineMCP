using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>rename_symbol</c>. A Roslyn-driven rename updates every reference across the
/// solution and returns only the resulting unified diff, keeping the emitted-token cost proportional
/// to the change rather than to the files touched. Defaults to preview mode; nothing is written to
/// disk unless the caller explicitly opts in.
/// </summary>
public class RenameSymbolResponse
{
    /// <summary>Name of the project the rename was anchored in.</summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>Fully-qualified name of the symbol that was renamed.</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The new name that was applied.</summary>
    [JsonPropertyName("newName")]
    public string NewName { get; set; } = string.Empty;

    /// <summary>Files that were (or, in preview mode, would be) modified by the rename.</summary>
    [JsonPropertyName("changedFiles")]
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>Unified diff of all changes across the solution.</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; set; } = string.Empty;

    /// <summary>Whether this was a preview-only operation (no files written).</summary>
    [JsonPropertyName("previewOnly")]
    public bool PreviewOnly { get; set; }

    /// <summary>Whether changes were actually written to disk (only true when <c>previewOnly</c> was explicitly false and there were changes).</summary>
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    /// <summary>Additional notes or warnings about the rename.</summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}
