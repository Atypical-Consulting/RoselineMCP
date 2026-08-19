using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// The one shared implementation of "get the diagnostics for this project" used by
/// <c>AnalyzeSolution</c>, <c>ListDiagnostics</c>, and <c>ApplyFixes</c>: compiler diagnostics
/// (<see cref="Compilation.GetDiagnostics(CancellationToken)"/>) combined with analyzer-driven
/// diagnostics computed via <see cref="CompilationWithAnalyzers"/>.
///
/// Analyzers come from two sources, deduplicated by analyzer type name (bundled instance wins):
/// the bundled catalog (<see cref="IAnalyzerCatalog"/> — Roslynator, shipped with RoselineMCP)
/// and the target project's own <see cref="Project.AnalyzerReferences"/> as loaded by
/// MSBuildWorkspace, so a repository's referenced analyzers (StyleCop, custom rules, …) surface
/// through the same tools.
///
/// Resilience: one broken analyzer never fails a tool call — per-analyzer exceptions are logged
/// and analysis continues (<c>onAnalyzerException</c>), and if the whole analyzer pass fails the
/// compiler diagnostics are still returned. Setting <c>RoselineMCP:RunAnalyzers</c> to
/// <see langword="false"/> skips the analyzer pass entirely (compiler-only, the fastest mode).
///
/// It does <em>not</em> stop source generators: they ship through the same
/// <see cref="Project.AnalyzerReferences"/> but run while building the <see cref="Compilation"/>
/// rather than as part of this pass, so they execute regardless of the switch. See
/// <c>SECURITY.md</c> — that distinction is a code-execution boundary, not a diagnostics detail.
/// </summary>
public class DiagnosticComputationService : IDiagnosticComputationService
{
    private readonly ILogger<DiagnosticComputationService> _logger;
    private readonly IAnalyzerCatalog _analyzerCatalog;
    private readonly RoselineMcpOptions _options;

    /// <summary>
    /// A compiler-only computation (no analyzers). Used as the fallback when no
    /// <see cref="IDiagnosticComputationService"/> is injected — production DI always supplies
    /// the analyzer-aware implementation; tests that only care about compiler diagnostics get
    /// the old, fast behavior without extra wiring.
    /// </summary>
    public static IDiagnosticComputationService CompilerOnly { get; } = new CompilerOnlyComputation();

    /// <summary>
    /// Initializes a new instance of the DiagnosticComputationService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="options">RoselineMCP options (honors <c>RunAnalyzers</c>).</param>
    /// <param name="analyzerCatalog">Catalog of the bundled analyzer assemblies.</param>
    public DiagnosticComputationService(
        ILogger<DiagnosticComputationService> logger,
        IOptions<RoselineMcpOptions> options,
        IAnalyzerCatalog analyzerCatalog)
    {
        _logger = logger;
        _analyzerCatalog = analyzerCatalog;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken = default)
    {
        var compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);

        if (!_options.RunAnalyzers)
        {
            return compilerDiagnostics;
        }

        var analyzers = CollectAnalyzers(project);
        if (analyzers.IsEmpty)
        {
            return compilerDiagnostics;
        }

        try
        {
            var analyzerOptions = new CompilationWithAnalyzersOptions(
                project.AnalyzerOptions,
                onAnalyzerException: (exception, analyzer, diagnostic) =>
                    _logger.LogWarning(exception,
                        "Analyzer {Analyzer} threw while analyzing {Project} ({DiagnosticId}); continuing without it",
                        analyzer.GetType().Name, project.Name, diagnostic.Id),
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false);

            var withAnalyzers = compilation.WithAnalyzers(analyzers, analyzerOptions);
            var analyzerDiagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);

            return compilerDiagnostics.AddRange(analyzerDiagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The analyzer pass as a whole failed (not just one analyzer) — degrade to
            // compiler-only rather than failing the tool call.
            _logger.LogWarning(ex,
                "Analyzer execution failed for project {Project}; returning compiler diagnostics only",
                project.Name);
            return compilerDiagnostics;
        }
    }

    /// <summary>
    /// Bundled analyzers first, then the project's own analyzer references, deduplicated by
    /// analyzer type full name so a target project that itself references Roslynator doesn't get
    /// every RCS diagnostic reported twice.
    /// </summary>
    private ImmutableArray<DiagnosticAnalyzer> CollectAnalyzers(Project project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

        foreach (var analyzer in _analyzerCatalog.Analyzers)
        {
            if (seen.Add(analyzer.GetType().FullName!))
            {
                builder.Add(analyzer);
            }
        }

        foreach (var reference in project.AnalyzerReferences)
        {
            ImmutableArray<DiagnosticAnalyzer> projectAnalyzers;
            try
            {
                projectAnalyzers = reference.GetAnalyzers(LanguageNames.CSharp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not load analyzers from {Reference}: {Message}",
                    reference.Display, ex.Message);
                continue;
            }

            foreach (var analyzer in projectAnalyzers)
            {
                if (seen.Add(analyzer.GetType().FullName!))
                {
                    builder.Add(analyzer);
                }
            }
        }

        return builder.ToImmutable();
    }

    private sealed class CompilerOnlyComputation : IDiagnosticComputationService
    {
        public Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
            Project project,
            Compilation compilation,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(compilation.GetDiagnostics(cancellationToken));
        }
    }
}
