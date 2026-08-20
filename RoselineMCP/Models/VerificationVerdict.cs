using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// The result of compiling a candidate solution in memory and comparing it against a baseline —
/// the payload of <c>check_compilation</c>, and the <c>verification</c> field on every write tool's
/// response.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is <see cref="Introduced"/>, never <see cref="Compiles"/>.</b> A repository that was
/// already broken before the edit reports <c>compiles: false</c> with an empty
/// <see cref="Introduced"/>, and the write still proceeds — refusing there would make RoselineMCP
/// unusable on exactly the branches an agent is sent to fix.
/// </para>
/// <para>
/// Every collection is omitted from the wire when empty and every counter when zero. That is not
/// tidiness: the verdict rides on every single edit, and eight always-present fields would spend
/// tokens on the overwhelmingly common "nothing to report" case. <see cref="ScopeComplete"/> is the
/// deliberate exception — it is always emitted, because <c>false</c> is its informative value and an
/// absent field would be indistinguishable from a full-coverage gate.
/// </para>
/// </remarks>
public class VerificationVerdict
{
    /// <summary>
    /// Whether the verified scope compiles — an <b>absolute</b> statement about the candidate, not a
    /// comparison. <see langword="null"/> (omitted) when no compilation was performed.
    /// </summary>
    [JsonPropertyName("compiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Compiles { get; set; }

    /// <summary>
    /// All compiler errors in scope, populated in <b>absolute</b> mode (no baseline). Without it
    /// <c>check_compilation</c> would answer "this does not compile" while carrying no diagnostics:
    /// <see cref="Introduced"/> and <see cref="Resolved"/> are empty by definition with no
    /// before-state. Omitted when empty.
    /// </summary>
    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DiagnosticDetail>? Errors { get; set; }

    /// <summary>
    /// Compiler errors the candidate change introduced — the errors that were not in the baseline.
    /// A non-empty list is what refuses a write. Omitted when empty.
    /// </summary>
    [JsonPropertyName("introduced")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DiagnosticDetail>? Introduced { get; set; }

    /// <summary>
    /// Compiler errors the candidate change removed — present in the baseline, gone in the
    /// candidate. Omitted when empty.
    /// </summary>
    [JsonPropertyName("resolved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DiagnosticDetail>? Resolved { get; set; }

    /// <summary>
    /// How many errors were already there before the change. Without this an agent landing on a
    /// broken branch believes the errors are its own and starts fixing them — a classic degradation
    /// loop. Omitted when zero.
    /// </summary>
    [JsonPropertyName("preexisting")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Preexisting { get; set; }

    /// <summary>
    /// How many diagnostics were dropped to honor the caller's <c>max</c>. A rename that breaks a
    /// public member of a base project produces thousands of binding errors; unbounded, the refusal
    /// would cost more tokens than the <c>dotnet build</c> output it replaces. Omitted when zero.
    /// </summary>
    [JsonPropertyName("omitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Omitted { get; set; }

    /// <summary>
    /// Names of the projects that were compiled: the changed projects plus their transitive
    /// dependents. Omitted when empty.
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Scope { get; set; }

    /// <summary>
    /// Whether the workspace could prove it holds every dependent of the changed projects.
    /// <see langword="false"/> when a bare <c>.csproj</c> was loaded with no containing solution:
    /// the write still proceeds, but the caller is told the gate was partial rather than handed a
    /// false green. Always emitted.
    /// </summary>
    [JsonPropertyName("scopeComplete")]
    public bool ScopeComplete { get; set; }

    /// <summary>
    /// Human-readable notes about the verdict itself — chiefly why <see cref="ScopeComplete"/> is
    /// <see langword="false"/>. Omitted when empty.
    /// </summary>
    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Notes { get; set; }
}
