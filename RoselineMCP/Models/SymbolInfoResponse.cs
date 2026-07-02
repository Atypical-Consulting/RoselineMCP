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

    /// <summary>Fully-qualified name including namespace and containing types.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Symbol kind in lowercase (e.g. <c>class</c>, <c>method</c>, <c>property</c>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Declared accessibility in lowercase.</summary>
    [JsonPropertyName("accessibility")]
    public string Accessibility { get; set; } = string.Empty;

    /// <summary>Declaration modifiers present on the symbol, e.g. <c>static</c>, <c>abstract</c>, <c>sealed</c>, <c>async</c>, <c>readonly</c>.</summary>
    [JsonPropertyName("modifiers")]
    public List<string> Modifiers { get; set; } = new();

    /// <summary>Human-readable signature of the symbol.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    /// <summary>Fully-qualified names of base types (for a type: its base class chain; for a member: empty).</summary>
    [JsonPropertyName("baseTypes")]
    public List<string> BaseTypes { get; set; } = new();

    /// <summary>Fully-qualified names of interfaces implemented by the type (empty for non-types).</summary>
    [JsonPropertyName("interfaces")]
    public List<string> Interfaces { get; set; } = new();

    /// <summary>XML documentation summary text for the symbol, if any.</summary>
    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    /// <summary>Absolute path to the source file declaring the symbol, or <c>null</c> if only available from metadata.</summary>
    [JsonPropertyName("definitionFile")]
    public string? DefinitionFile { get; set; }

    /// <summary>1-based line number of the symbol's declaration, or <c>null</c> if metadata-only.</summary>
    [JsonPropertyName("definitionLine")]
    public int? DefinitionLine { get; set; }

    /// <summary>The exact source text of the symbol's declaration, included only when <c>includeSource</c> was requested.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
