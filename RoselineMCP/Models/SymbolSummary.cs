using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Compact, token-economical description of a single C# symbol. Deliberately carries only the
/// structure an AI agent needs to reason about a symbol — its name, kind, signature, and where it
/// lives — instead of the full source text of its containing file. Shared across the navigation
/// tools (search, references, implementations, type hierarchy).
/// </summary>
/// <remarks>
/// Optional fields are omitted from the JSON when null (they cost tokens for no information). The
/// single-file outline of <c>search_symbols</c> deliberately omits <see cref="FullName"/>,
/// <see cref="Accessibility"/>, and <see cref="File"/>: the file is already on the response, and
/// accessibility/return type are already inside <see cref="Signature"/> — so repeating them on
/// every symbol only inflates the output. The project-wide search populates them, since results
/// then span many files.
/// </remarks>
public class SymbolSummary
{
    /// <summary>Simple (unqualified) name of the symbol, e.g. <c>GetUser</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Fully-qualified name including namespace and containing types. Omitted in the file outline.</summary>
    [JsonPropertyName("fullName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullName { get; set; }

    /// <summary>Symbol kind in lowercase, e.g. <c>class</c>, <c>interface</c>, <c>method</c>, <c>property</c>, <c>field</c>, <c>enum</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Human-readable signature, e.g. <c>public Task&lt;User&gt; GetUser(int id)</c>. Empty for kinds without a meaningful signature.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    /// <summary>Declared accessibility in lowercase. Omitted in the file outline (it is already part of <see cref="Signature"/>).</summary>
    [JsonPropertyName("accessibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accessibility { get; set; }

    /// <summary>Absolute path to the source file declaring the symbol. Omitted for metadata-only symbols and in the file outline (it is on the response).</summary>
    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    /// <summary>1-based line number of the symbol's declaration, or omitted for metadata-only symbols.</summary>
    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; set; }

    /// <summary>Fully-qualified name of the type that contains this symbol, or omitted for top-level types/namespaces.</summary>
    [JsonPropertyName("containingType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainingType { get; set; }
}
