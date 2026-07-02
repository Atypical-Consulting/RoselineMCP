using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>find_implementations</c>. For an interface or abstract/virtual member,
/// lists the concrete implementors/overriders; for a class, lists derived types — all as compact
/// <see cref="SymbolSummary"/> entries rather than full source.
/// </summary>
public class ImplementationsResponse
{
    /// <summary>Simple name of the symbol whose implementations were requested.</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Fully-qualified name of the symbol whose implementations were requested.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Kind of the queried symbol in lowercase (e.g. <c>interface</c>, <c>class</c>, <c>method</c>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Total number of implementations/overrides/derived types found before the <c>max</c> cap.</summary>
    [JsonPropertyName("totalFound")]
    public int TotalFound { get; set; }

    /// <summary>Whether <see cref="TotalFound"/> exceeded <c>max</c>, meaning <see cref="Implementations"/> is truncated.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>The implementing/overriding/derived symbols (capped at the requested maximum).</summary>
    [JsonPropertyName("implementations")]
    public List<SymbolSummary> Implementations { get; set; } = new();
}
