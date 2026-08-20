using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

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
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.
    /// Auto-discovery starts from the server's working directory, so this is how a caller confirms
    /// which checkout answered — e.g. a git worktree versus its main checkout.
    /// </summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

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