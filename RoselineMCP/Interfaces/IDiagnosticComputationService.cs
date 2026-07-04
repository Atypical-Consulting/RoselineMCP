using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Computes the full set of diagnostics for a project: compiler diagnostics plus — unless
/// disabled via <c>RoselineMCP:RunAnalyzers</c> — analyzer-driven diagnostics from the bundled
/// analyzers (<see cref="IAnalyzerCatalog"/>) and the target project's own analyzer references.
/// The single shared implementation behind <c>AnalyzeSolution</c>, <c>ListDiagnostics</c>, and
/// <c>ApplyFixes</c>, so all three surface the same diagnostics.
/// </summary>
public interface IDiagnosticComputationService
{
    /// <summary>
    /// Returns compiler + analyzer diagnostics for <paramref name="compilation"/>.
    /// </summary>
    /// <param name="project">The project the compilation was produced from (supplies analyzer
    /// references and analyzer options).</param>
    /// <param name="compilation">The project's compilation. Must be the compilation of
    /// <paramref name="project"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken = default);
}
