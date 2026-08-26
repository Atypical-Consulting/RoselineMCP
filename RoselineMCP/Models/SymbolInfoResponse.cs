using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>get_symbol_info</c>. Bundles a symbol's declaration metadata, signature,
/// base types/interfaces, XML documentation, definition location, and (optionally) the definition's
/// source — the token-cheap substitute for reading the whole file to "go to definition".
/// </summary>
public class SymbolInfoResponse
{
    /// <summary>Simple (unqualified) name of the resolved symbol.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.
    /// Auto-discovery starts from the server's working directory, so this is how a caller confirms
    /// which checkout answered — e.g. a git worktree versus its main checkout.
    /// </summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

    /// <summary>Fully-qualified name including namespace and containing types.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Symbol kind in lowercase (e.g. <c>class</c>, <c>method</c>, <c>property</c>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Declaration modifiers present on the symbol, e.g. <c>static</c>, <c>abstract</c>, <c>sealed</c>, <c>async</c>, <c>readonly</c>. Omitted when none apply.</summary>
    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Modifiers { get; set; }

    /// <summary>Human-readable signature of the symbol.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    /// <summary>Fully-qualified names of base types (a type's base-class chain). Omitted for members and types with no base chain.</summary>
    [JsonPropertyName("baseTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? BaseTypes { get; set; }

    /// <summary>Fully-qualified names of interfaces implemented by the type. Omitted for non-types and types implementing none.</summary>
    [JsonPropertyName("interfaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Interfaces { get; set; }

    /// <summary>XML documentation summary text for the symbol. Omitted when the symbol has no XML docs.</summary>
    [JsonPropertyName("documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Documentation { get; set; }

    /// <summary>Source file (relative to <c>resolvedPath</c>'s directory) declaring the symbol. Omitted if only available from metadata.</summary>
    [JsonPropertyName("definitionFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionFile { get; set; }

    /// <summary>1-based line number of the symbol's declaration. Omitted if metadata-only.</summary>
    [JsonPropertyName("definitionLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefinitionLine { get; set; }

    /// <summary>The exact source text of the symbol's declaration, included only when <c>includeSource</c> was requested; omitted otherwise.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
}
