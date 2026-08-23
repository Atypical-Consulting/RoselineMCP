using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// What the analyzer pass could and could not load — the <c>analyzerLoad</c> block of the three
/// diagnostics responses (<c>list_diagnostics</c>, <c>analyze_solution</c>, <c>apply_fixes</c>).
/// </summary>
/// <remarks>
/// <para>
/// Roslyn reports an analyzer reference it cannot load by an <b>empty array</b>, not an exception:
/// an analyzer built against a newer <c>Microsoft.CodeAnalysis</c> than the one in-process yields
/// zero analyzers and raises <c>AnalyzerFileReference.AnalyzerLoadFailed</c>, which nothing had to
/// subscribe to. A diagnostics pass that guarded only the throwing path therefore served a smaller
/// diagnostic set with no warning, no note and no field — degraded coverage that looked exactly like
/// clean coverage. This report is the remedy: every reference that contributed nothing is
/// <b>named</b>, with Roslyn's reason when it gave one.
/// </para>
/// <para>
/// The block is omitted from a response when every consulted reference contributed, so an absent
/// block means "nothing to report" and a present one always names something. When
/// <c>RoselineMCP:RunAnalyzers</c> is <see langword="false"/> it is present with
/// <see cref="ReferencesConsulted"/> = 0, so the caller can tell "analyzers off" from "all fine".
/// </para>
/// </remarks>
public class AnalyzerLoadReport
{
    /// <summary>
    /// How many of the target project's analyzer references were asked for C# analyzers. Zero when
    /// the analyzer pass is disabled (<c>RoselineMCP:RunAnalyzers = false</c>).
    /// </summary>
    [JsonPropertyName("referencesConsulted")]
    public int ReferencesConsulted { get; set; }

    /// <summary>
    /// How many of those references yielded at least one analyzer. Anything below
    /// <see cref="ReferencesConsulted"/> is explained, reference by reference, in <see cref="Notes"/>.
    /// </summary>
    [JsonPropertyName("referencesContributing")]
    public int ReferencesContributing { get; set; }

    /// <summary>
    /// Total number of distinct analyzers that ran — the bundled catalog plus the project's own
    /// references, deduplicated by analyzer type.
    /// </summary>
    [JsonPropertyName("analyzersLoaded")]
    public int AnalyzersLoaded { get; set; }

    /// <summary>
    /// One entry per reference that contributed nothing, in project order. Empty when every
    /// consulted reference contributed.
    /// </summary>
    [JsonPropertyName("notes")]
    public List<AnalyzerLoadNote> Notes { get; set; } = [];

    /// <summary>
    /// Whether this report belongs on a response: it names a reference that contributed nothing,
    /// or no reference was consulted at all (the analyzer pass is off, or the project carries
    /// none) — the case a caller must be able to tell from "every reference contributed".
    /// <see langword="false"/> is the clean case, and the block is then omitted from the wire.
    /// </summary>
    [JsonIgnore]
    public bool HasSomethingToReport => Notes.Count > 0 || ReferencesConsulted == 0;

    /// <summary>
    /// The report to put on a response: <paramref name="report"/> itself when it has something to
    /// report, otherwise <see langword="null"/> so the clean case stays silent on the wire.
    /// </summary>
    /// <param name="report">The report produced by the diagnostics pass.</param>
    public static AnalyzerLoadReport? ForResponse(AnalyzerLoadReport report) =>
        report.HasSomethingToReport ? report : null;

    /// <summary>
    /// Combines the per-project reports of a multi-project analysis (<c>analyze_solution</c>) into
    /// one: the counters are summed over the analyzed projects — so they count reference
    /// <em>consultations</em>, not distinct references — and the notes are deduplicated by
    /// reference and reason, since the same reference (the SDK's own analyzer set, say) recurs in
    /// every project and need only be named once.
    /// </summary>
    /// <param name="reports">The per-project reports, in any order.</param>
    public static AnalyzerLoadReport Merge(IEnumerable<AnalyzerLoadReport> reports)
    {
        var merged = new AnalyzerLoadReport();
        var seen = new HashSet<(string Reference, string Reason, string? ErrorCode)>();
        foreach (var report in reports)
        {
            merged.ReferencesConsulted += report.ReferencesConsulted;
            merged.ReferencesContributing += report.ReferencesContributing;
            merged.AnalyzersLoaded += report.AnalyzersLoaded;
            foreach (var note in report.Notes)
            {
                if (seen.Add((note.Reference, note.Reason, note.ErrorCode)))
                {
                    merged.Notes.Add(note);
                }
            }
        }

        return merged;
    }
}

/// <summary>
/// One analyzer reference that contributed no C# analyzers, and why.
/// </summary>
public class AnalyzerLoadNote
{
    /// <summary>
    /// <see cref="Reason"/> when Roslyn raised <c>AnalyzerLoadFailed</c> for the reference: the
    /// assembly or one of its analyzer types could not be loaded. <see cref="ErrorCode"/> and
    /// <see cref="Message"/> carry Roslyn's own diagnosis.
    /// </summary>
    public const string LoadFailure = "load-failure";

    /// <summary>
    /// <see cref="Reason"/> when the reference loaded and simply declares no C# analyzer — a
    /// source-generator-only assembly, a code-fix-only assembly, or an analyzer's support library.
    /// Accurate, not alarming.
    /// </summary>
    public const string NoCSharpAnalyzers = "no C# analyzers";

    /// <summary>
    /// <see cref="Reason"/> when <c>GetAnalyzers</c> itself threw. <see cref="Message"/> carries the
    /// exception message.
    /// </summary>
    public const string Exception = "exception";

    /// <summary>
    /// The reference's display name (<c>AnalyzerReference.Display</c> — the assembly's simple name
    /// for a file reference).
    /// </summary>
    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    /// <summary>
    /// Why the reference contributed nothing: <see cref="LoadFailure"/>,
    /// <see cref="NoCSharpAnalyzers"/> or <see cref="Exception"/>.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Roslyn's failure classification for a <see cref="LoadFailure"/> (e.g.
    /// <c>ReferencesNewerCompiler</c>, <c>UnableToLoadAnalyzer</c>, <c>UnableToCreateAnalyzer</c>).
    /// Omitted otherwise.
    /// </summary>
    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The failure message Roslyn (or the thrown exception) gave. Omitted when there is none —
    /// <see cref="NoCSharpAnalyzers"/> has nothing more to say.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
