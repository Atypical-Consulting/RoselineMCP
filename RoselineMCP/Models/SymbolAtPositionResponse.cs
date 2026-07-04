using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>get_symbol_at_position</c>. Identifies the symbol living at a
/// <c>file:line(:column)</c> position — the bridge from a diagnostic, stack trace, or grep hit to
/// the symbol-name-based navigation tools, without reading the file. Optional fields are omitted
/// from the JSON when empty/absent (token-lean convention shared with
/// <see cref="SymbolInfoResponse"/>).
/// </summary>
public class SymbolAtPositionResponse
{
    /// <summary>Simple (unqualified) name of the resolved symbol.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Fully-qualified name including namespace and containing types.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Symbol kind in lowercase (e.g. <c>class</c>, <c>method</c>, <c>local</c>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Human-readable signature of the symbol (already carries the accessibility keyword).</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    /// <summary>Simple (unqualified) name of the containing type. Omitted for top-level types and namespaces.</summary>
    [JsonPropertyName("containingType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainingType { get; set; }

    /// <summary>Whether the requested position sits on the symbol's own declaration (as opposed to a use site).</summary>
    [JsonPropertyName("isDeclaration")]
    public bool IsDeclaration { get; set; }

    /// <summary>XML documentation summary text for the symbol. Omitted when the symbol has no XML docs.</summary>
    [JsonPropertyName("documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Documentation { get; set; }

    /// <summary>Source file (solution-root-relative) declaring the symbol. Omitted if only available from metadata.</summary>
    [JsonPropertyName("definitionFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionFile { get; set; }

    /// <summary>1-based line number of the symbol's declaration. Omitted if metadata-only.</summary>
    [JsonPropertyName("definitionLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefinitionLine { get; set; }
}
