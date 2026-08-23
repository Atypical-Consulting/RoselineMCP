using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoselineMCP.Models;

/// <summary>
/// The outcome of one diagnostics pass (<c>IDiagnosticComputationService.GetDiagnosticsAsync</c>):
/// the diagnostics themselves, and the account of which analyzer references produced them — so a
/// caller can tell a complete answer from a degraded one.
/// </summary>
public sealed class DiagnosticComputationResult
{
    /// <summary>Compiler diagnostics plus whatever the analyzers that loaded reported.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; init; } = ImmutableArray<Diagnostic>.Empty;

    /// <summary>
    /// Which analyzer references were consulted, which contributed, and — by name — which did not.
    /// Never <see langword="null"/>; a compiler-only pass reports zero references consulted.
    /// </summary>
    public AnalyzerLoadReport AnalyzerLoad { get; init; } = new();
}
