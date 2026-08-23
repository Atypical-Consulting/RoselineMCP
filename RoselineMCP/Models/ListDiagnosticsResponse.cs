using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// Response model for listing project diagnostics.
/// </summary>
public class ListDiagnosticsResponse
{
    /// <summary>
    /// Name of the analyzed project.
    /// </summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) that was actually resolved and loaded.
    /// Auto-discovery starts from the server's working directory, so this is how a caller confirms
    /// which checkout answered — e.g. a git worktree versus its main checkout.
    /// </summary>
    [JsonPropertyName("resolvedPath")]
    public string ResolvedPath { get; set; } = string.Empty;

    /// <summary>
    /// Total number of diagnostics found.
    /// </summary>
    [JsonPropertyName("totalDiagnostics")]
    public int TotalDiagnostics { get; set; }

    /// <summary>
    /// List of detailed diagnostic information.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public List<DiagnosticDetail> Diagnostics { get; set; } = new();

    /// <summary>
    /// Statistical breakdown of diagnostics.
    /// </summary>
    [JsonPropertyName("stats")]
    public DiagnosticStats Stats { get; set; } = new();

    /// <summary>
    /// List of diagnostic IDs that can be automatically fixed.
    /// </summary>
    [JsonPropertyName("suggestedFixableIds")]
    public List<string> SuggestedFixableIds { get; set; } = new();

    /// <summary>
    /// Which of the project's analyzer references could not contribute, and why. Omitted when every
    /// consulted reference contributed — an absent block means "nothing to report", a present one
    /// always names something (or reports that no reference was consulted at all, when the analyzer
    /// pass is off). Without it, an analyzer that failed to load silently shrank this response.
    /// </summary>
    [JsonPropertyName("analyzerLoad")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalyzerLoadReport? AnalyzerLoad { get; set; }
}