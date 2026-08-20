using System.Text.Json.Serialization;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for code fix application operations.
/// </summary>
public class ApplyFixesResponse : IWriteToolResponse
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

    /// <summary>
    /// Whether the fixes actually reached disk (only true when <c>previewOnly</c> was explicitly
    /// false, there were changes, and the compile gate did not refuse them).
    /// </summary>
    /// <remarks>
    /// Without this field a refusal is indistinguishable from a success: the response still carries
    /// <c>previewOnly: false</c>, a patch and a <c>fixedCount</c>, and an agent reading it would
    /// move on believing the fixes landed.
    /// </remarks>
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    /// <summary>
    /// The compiler's verdict on the fixed solution, computed in memory before anything touched
    /// disk. A code fix is generated code the caller never wrote, so "the fixer said it was fine"
    /// is exactly the assurance worth checking. When <c>introduced</c> is non-empty and the caller
    /// did not pass <c>allowIntroducedErrors</c>, the fixes were <b>refused</b>.
    /// </summary>
    [JsonPropertyName("verification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerificationVerdict? Verification { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public bool HasChanges => ChangedFiles.Count > 0;
}
