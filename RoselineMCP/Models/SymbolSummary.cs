using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Compact, token-economical description of a single C# symbol. Deliberately carries only the
/// structure an AI agent needs to reason about a symbol — its name, kind, signature, and where it
/// lives — instead of the full source text of its containing file. Shared across the navigation
/// tools (search, references, implementations, type hierarchy) so callers see one consistent shape.
/// </summary>
public class SymbolSummary
{
    /// <summary>Simple (unqualified) name of the symbol, e.g. <c>GetUser</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Fully-qualified name including namespace and containing types, e.g. <c>Acme.Users.UserService.GetUser</c>.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Symbol kind in lowercase, e.g. <c>class</c>, <c>interface</c>, <c>method</c>, <c>property</c>, <c>field</c>, <c>enum</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Human-readable signature, e.g. <c>public Task&lt;User&gt; GetUser(int id)</c>. Empty for kinds without a meaningful signature.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    /// <summary>Declared accessibility in lowercase, e.g. <c>public</c>, <c>internal</c>, <c>private</c>, or <c>notapplicable</c>.</summary>
    [JsonPropertyName("accessibility")]
    public string Accessibility { get; set; } = string.Empty;

    /// <summary>Absolute path to the source file declaring the symbol, or <c>null</c> for symbols only available from metadata.</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }

    /// <summary>1-based line number of the symbol's declaration, or <c>null</c> for metadata-only symbols.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; set; }

    /// <summary>Fully-qualified name of the type that contains this symbol, or <c>null</c> for top-level types/namespaces.</summary>
    [JsonPropertyName("containingType")]
    public string? ContainingType { get; set; }
}
