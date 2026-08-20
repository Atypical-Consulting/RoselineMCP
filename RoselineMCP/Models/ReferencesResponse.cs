using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>find_references</c>. Lists every use site of a symbol as a location plus a
/// one-line snippet, so an agent can survey usage across the solution without opening each file.
/// </summary>
public class ReferencesResponse
{
    /// <summary>Simple name of the symbol whose references were found.</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.
    /// Auto-discovery starts from the server's working directory, so this is how a caller confirms
    /// which checkout answered — e.g. a git worktree versus its main checkout.
    /// </summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

    /// <summary>Fully-qualified name of the symbol whose references were found.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Total number of reference locations found before the <c>max</c> cap was applied.</summary>
    [JsonPropertyName("totalReferences")]
    public int TotalReferences { get; set; }

    /// <summary>Whether <see cref="TotalReferences"/> exceeded <c>max</c>, meaning <see cref="References"/> is truncated.</summary>
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    /// <summary>The reference locations (capped at the requested maximum).</summary>
    [JsonPropertyName("references")]
    public List<ReferenceLocation> References { get; set; } = new();
}

/// <summary>A single reference (use site) of a symbol.</summary>
public class ReferenceLocation
{
    /// <summary>File (solution-root-relative) containing the reference.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>1-based line number of the reference.</summary>
    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>The trimmed source line containing the reference, for at-a-glance context.</summary>
    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = string.Empty;
}
