using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>search_symbols</c>. Returns a flat list of matching symbols (or, when a
/// single file is targeted with no query, that file's structural outline) so an agent can locate
/// code by name/shape without reading whole files.
/// </summary>
public class SymbolSearchResponse
{
    /// <summary>Name of the project that was searched.</summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.
    /// Auto-discovery starts from the server's working directory, so this is how a caller confirms
    /// which checkout answered — e.g. a git worktree versus its main checkout.
    /// </summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

    /// <summary>The search query that was applied (wildcard/substring pattern), or <c>null</c> when a file outline was requested.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>The file whose outline was returned, when the request targeted a single file; otherwise <c>null</c>.</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }

    /// <summary>Total number of matching symbols found before the <c>max</c> cap was applied.</summary>
    [JsonPropertyName("totalFound")]
    public int TotalFound { get; set; }

    /// <summary>Whether <see cref="TotalFound"/> exceeded <c>max</c>, meaning <see cref="Symbols"/> is a truncated view.</summary>
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    /// <summary>The matching symbols (capped at the requested maximum).</summary>
    [JsonPropertyName("symbols")]
    public List<SymbolSummary> Symbols { get; set; } = new();
}
