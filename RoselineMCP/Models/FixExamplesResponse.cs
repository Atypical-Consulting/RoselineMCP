using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response of <c>suggest_fix_examples</c>: the rule's own account of itself plus clean,
/// shape-matched sites in the same solution where the diagnostic does not fire.
/// </summary>
public class FixExamplesResponse
{
    /// <summary>Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.</summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

    /// <summary>The rule (diagnostic descriptor) being explained.</summary>
    [JsonPropertyName("rule")]
    public RuleInfo Rule { get; set; } = new();

    /// <summary>True when a registered code-fix provider exists, so <c>apply_fixes</c> is the better route.</summary>
    [JsonPropertyName("hasFixer")]
    public bool HasFixer { get; set; }

    /// <summary>The occurrence the examples were retrieved for; absent when the ID does not fire.</summary>
    [JsonPropertyName("alert")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlertSite? Alert { get; set; }

    /// <summary>Clean sites of the same shape, best match first.</summary>
    [JsonPropertyName("examples")]
    public List<FixExample> Examples { get; set; } = new();

    /// <summary>Number of shape-candidate syntax nodes examined.</summary>
    [JsonPropertyName("candidatesScanned")]
    public int CandidatesScanned { get; set; }

    /// <summary>True when the scan hit <c>maxCandidates</c> or more clean sites exist than <c>maxExamples</c>.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>Plain-language account of fallbacks and empty results.</summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

/// <summary>A diagnostic rule, read off its <c>DiagnosticDescriptor</c>.</summary>
public class RuleInfo
{
    /// <summary>Diagnostic ID, e.g. <c>CS0121</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Short title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Rule category.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Default severity, lowercase.</summary>
    [JsonPropertyName("defaultSeverity")]
    public string DefaultSeverity { get; set; } = string.Empty;

    /// <summary>Longer description, when the rule has one.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Help link, when the rule has one.</summary>
    [JsonPropertyName("helpLinkUri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HelpLinkUri { get; set; }
}

/// <summary>The occurrence of the diagnostic that examples were retrieved for.</summary>
public class AlertSite
{
    /// <summary>File, relative to the resolved path's directory, forward slashes.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>1-based line.</summary>
    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>The trimmed source line.</summary>
    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = string.Empty;

    /// <summary>The shape key used for retrieval.</summary>
    [JsonPropertyName("shapeKey")]
    public string ShapeKey { get; set; } = string.Empty;
}

/// <summary>A clean site matching the alert's shape.</summary>
public class FixExample
{
    /// <summary>File, relative to the resolved path's directory, forward slashes.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>1-based line.</summary>
    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>The trimmed source line.</summary>
    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Which shape components matched: <c>kinds+symbol</c> or <c>kinds</c>.</summary>
    [JsonPropertyName("matchedOn")]
    public string MatchedOn { get; set; } = string.Empty;
}
