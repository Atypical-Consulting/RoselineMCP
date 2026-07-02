using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>get_type_hierarchy</c>. Returns a type's base-class chain, implemented
/// interfaces, and derived types as compact summaries — the structural relationships an agent needs
/// without reading the declaring files.
/// </summary>
public class TypeHierarchyResponse
{
    /// <summary>Simple name of the queried type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Fully-qualified name of the queried type.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Direction that was requested: <c>base</c>, <c>derived</c>, or <c>both</c>.</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    /// <summary>Base-class chain from the immediate base up to (but excluding) <c>object</c>, when base was requested.</summary>
    [JsonPropertyName("baseTypes")]
    public List<SymbolSummary>? BaseTypes { get; set; }

    /// <summary>Interfaces implemented (directly or transitively) by the type, when base was requested.</summary>
    [JsonPropertyName("interfaces")]
    public List<SymbolSummary>? Interfaces { get; set; }

    /// <summary>Types that derive from the queried type, when derived was requested (capped at <c>max</c>).</summary>
    [JsonPropertyName("derivedTypes")]
    public List<SymbolSummary>? DerivedTypes { get; set; }

    /// <summary>Whether the derived-type list was capped at <c>max</c> (more derived types exist than were returned).</summary>
    [JsonPropertyName("derivedTypesTruncated")]
    public bool DerivedTypesTruncated { get; set; }
}
