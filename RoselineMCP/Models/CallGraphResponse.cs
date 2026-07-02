using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for <c>get_call_graph</c>. Returns a depth-bounded tree of callers and/or callees
/// of a method with cycle detection, letting an agent trace control flow without reading any bodies.
/// </summary>
public class CallGraphResponse
{
    /// <summary>Simple name of the root method the graph was built around.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>Fully-qualified name of the root method.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Direction the graph was traversed: <c>callers</c>, <c>callees</c>, or <c>both</c>.</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    /// <summary>Maximum traversal depth that was applied.</summary>
    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    /// <summary>Methods that call the root method (present when direction is <c>callers</c> or <c>both</c>).</summary>
    [JsonPropertyName("callers")]
    public List<CallGraphNode>? Callers { get; set; }

    /// <summary>Methods called by the root method (present when direction is <c>callees</c> or <c>both</c>).</summary>
    [JsonPropertyName("callees")]
    public List<CallGraphNode>? Callees { get; set; }
}

/// <summary>A node in a call graph: one method plus its onward edges up to the depth limit.</summary>
public class CallGraphNode
{
    /// <summary>Fully-qualified name of the method at this node.</summary>
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Signature of the method at this node.</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    /// <summary>Absolute path to the file declaring the method, or <c>null</c> for metadata-only methods.</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }

    /// <summary>1-based declaration line of the method, or <c>null</c> for metadata-only methods.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; set; }

    /// <summary>
    /// <c>true</c> when this node was reached again along the current path (a cycle) or the depth
    /// limit stopped further expansion, so <see cref="Children"/> is intentionally omitted.
    /// </summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>The next level of callers/callees, or <c>null</c> when this node was not expanded.</summary>
    [JsonPropertyName("children")]
    public List<CallGraphNode>? Children { get; set; }
}
