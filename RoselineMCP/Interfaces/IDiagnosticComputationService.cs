using Microsoft.CodeAnalysis;
using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Computes the full set of diagnostics for a project: compiler diagnostics plus — unless
/// disabled via <c>RoselineMCP:RunAnalyzers</c> — analyzer-driven diagnostics from the bundled
/// analyzers (<see cref="IAnalyzerCatalog"/>) and the target project's own analyzer references.
/// The single shared implementation behind <c>AnalyzeSolution</c>, <c>ListDiagnostics</c>, and
/// <c>ApplyFixes</c>, so all three surface the same diagnostics — and the same account of which
/// analyzer references could not be loaded (<see cref="DiagnosticComputationResult.AnalyzerLoad"/>).
/// </summary>
public interface IDiagnosticComputationService
{
    /// <summary>
    /// Returns compiler + analyzer diagnostics for <paramref name="compilation"/>, together with
    /// the analyzer-load report naming every reference that contributed nothing.
    /// </summary>
    /// <param name="project">The project the compilation was produced from (supplies analyzer
    /// references and analyzer options).</param>
    /// <param name="compilation">The project's compilation. Must be the compilation of
    /// <paramref name="project"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<DiagnosticComputationResult> GetDiagnosticsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The analyzer-load report for <paramref name="project"/> <em>without</em> running a
    /// diagnostics pass: which references can be loaded and which cannot. For a caller that
    /// found nothing to analyze (an <c>apply_fixes</c> whose IDs have no fixer) and must still
    /// tell "no fixer exists" apart from "the reference carrying it never loaded". Cheap after
    /// the first call — Roslyn caches each reference's answer.
    /// </summary>
    /// <param name="project">The project whose analyzer references are consulted.</param>
    AnalyzerLoadReport DescribeAnalyzerLoad(Project project);
}
