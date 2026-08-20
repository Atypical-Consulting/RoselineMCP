using System.Text.Json.Serialization;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>edit_member</c>. A surgical, member-level edit (replace/add/delete) returns
/// only the resulting unified diff and the affected file — never a whole-file rewrite — so the tokens
/// an agent must emit to change code stay small. Defaults to preview mode; nothing is written to disk
/// unless the caller explicitly opts in.
/// </summary>
public class EditMemberResponse : IWriteToolResponse
{
    /// <summary>Name of the project the edit targeted.</summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.
    /// Auto-discovery starts from the server's working directory, so this is how a caller confirms
    /// which checkout answered — e.g. a git worktree versus its main checkout.
    /// </summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

    /// <summary>The operation performed: <c>replace</c>, <c>add</c>, or <c>delete</c>.</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    /// <summary>Fully-qualified name of the member (or container type, for <c>add</c>) the edit targeted.</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>Files that were (or, in preview mode, would be) modified.</summary>
    [JsonPropertyName("changedFiles")]
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>Unified diff of the edit.</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; set; } = string.Empty;

    /// <summary>Whether this was a preview-only operation (no files written).</summary>
    [JsonPropertyName("previewOnly")]
    public bool PreviewOnly { get; set; }

    /// <summary>Whether changes were actually written to disk (only true when <c>previewOnly</c> was explicitly false and there were changes).</summary>
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    /// <summary>
    /// The compiler's verdict on this change, computed in memory before anything touched disk. When
    /// <c>introduced</c> is non-empty and the caller did not pass <c>allowIntroducedErrors</c>, the
    /// edit was <b>refused</b>: <c>applied</c> is false and no file was written.
    /// </summary>
    [JsonPropertyName("verification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerificationVerdict? Verification { get; set; }

    /// <summary>Additional notes or warnings about the edit.</summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    /// <inheritdoc />
    [JsonIgnore]
    public bool HasChanges => ChangedFiles.Count > 0;
}
