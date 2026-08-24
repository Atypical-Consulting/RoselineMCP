using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

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
/// Degradation is <b>named</b>, never silent. Roslyn reports an analyzer reference it cannot load
/// — an assembly built against a newer <c>Microsoft.CodeAnalysis</c> than the one in-process is
/// the universal case — by raising <see cref="AnalyzerFileReference.AnalyzerLoadFailed"/> and
/// returning an <em>empty array</em>, not by throwing. This service subscribes to that event for
/// the duration of each <c>GetAnalyzers</c> call and reports every reference that contributed
/// nothing, with Roslyn's reason, in <see cref="DiagnosticComputationResult.AnalyzerLoad"/>
/// (the same guarantee <see cref="AnalyzerCatalog"/> gives for the bundled folder, extended past
/// the bundle's edge). Roslyn raises the event only on its <em>first</em> attempt and caches the
/// empty answer, so a failure observed for a reference object is remembered for that object's
/// lifetime — the workspace cache hands the same references to every subsequent call.
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
    /// Load failures observed per reference object. Roslyn raises <c>AnalyzerLoadFailed</c> once
    /// and then serves the cached empty array silently, so without this a second consultation of
    /// the same (cached-workspace) reference would be misreported as "no C# analyzers".
    /// </summary>
    private readonly ConditionalWeakTable<AnalyzerReference, AnalyzerLoadNote> _rememberedFailures = new();

    /// <summary>
    /// One lock per file reference, so the observe-then-remember sequence in
    /// <see cref="LoadAnalyzers"/> cannot interleave with a concurrent consultation of the same
    /// reference (a solution's projects are analyzed in parallel, and the workspace cache shares
    /// reference objects across tool calls).
    /// </summary>
    private readonly ConditionalWeakTable<AnalyzerReference, object> _referenceLocks = new();

    /// <summary>
    /// A compiler-only computation (no analyzers). Used as the fallback when no
    /// <see cref="IDiagnosticComputationService"/> is injected — production DI always supplies
    /// the analyzer-aware implementation; tests that only care about compiler diagnostics get
    /// the old, fast behavior without extra wiring. Its report consults zero references.
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
    public async Task<DiagnosticComputationResult> GetDiagnosticsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken = default)
    {
        var compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);

        if (!_options.RunAnalyzers)
        {
            return new DiagnosticComputationResult { Diagnostics = compilerDiagnostics };
        }

        var (analyzers, report) = CollectAnalyzers(project);
        if (analyzers.IsEmpty)
        {
            return new DiagnosticComputationResult { Diagnostics = compilerDiagnostics, AnalyzerLoad = report };
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

            return new DiagnosticComputationResult
            {
                Diagnostics = compilerDiagnostics.AddRange(analyzerDiagnostics),
                AnalyzerLoad = report
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The analyzer pass as a whole failed (not just one analyzer) — degrade to
            // compiler-only rather than failing the tool call, and say so in the report: every
            // reference loaded, yet every analyzer diagnostic is missing, and a report that only
            // counted references would read as "all fine".
            _logger.LogWarning(ex,
                "Analyzer execution failed for project {Project}; returning compiler diagnostics only",
                project.Name);
            report.Notes.Add(new AnalyzerLoadNote
            {
                Reference = AnalyzerLoadNote.AnalyzerPass,
                Reason = AnalyzerLoadNote.Exception,
                Message = $"the analyzer pass failed and every analyzer diagnostic was dropped: {ex.Message}"
            });
            return new DiagnosticComputationResult { Diagnostics = compilerDiagnostics, AnalyzerLoad = report };
        }
    }

    /// <inheritdoc/>
    public AnalyzerLoadReport DescribeAnalyzerLoad(Project project) =>
        _options.RunAnalyzers ? CollectAnalyzers(project).Report : new AnalyzerLoadReport();

    /// <summary>
    /// Bundled analyzers first, then the project's own analyzer references, deduplicated by
    /// analyzer type full name so a target project that itself references Roslynator doesn't get
    /// every RCS diagnostic reported twice. Every reference that yields nothing is named in the
    /// report: with Roslyn's load-failure diagnosis when it raised one, as
    /// <see cref="AnalyzerLoadNote.NoCSharpAnalyzers"/> when it loaded and simply declares none,
    /// or as <see cref="AnalyzerLoadNote.Exception"/> when <c>GetAnalyzers</c> itself threw. A
    /// reference that loaded only some of its analyzers keeps the ones it loaded <em>and</em> is
    /// named, so a partial failure is neither dropped nor silent.
    /// </summary>
    private (ImmutableArray<DiagnosticAnalyzer> Analyzers, AnalyzerLoadReport Report) CollectAnalyzers(Project project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var report = new AnalyzerLoadReport { AnalyzersRan = true, ReferencesConsulted = project.AnalyzerReferences.Count };

        foreach (var analyzer in _analyzerCatalog.Analyzers)
        {
            if (seen.Add(analyzer.GetType().FullName!))
            {
                builder.Add(analyzer);
            }
        }

        foreach (var reference in project.AnalyzerReferences)
        {
            var projectAnalyzers = LoadAnalyzers(reference, out var note);
            if (note is not null)
            {
                report.Notes.Add(note);
            }

            if (projectAnalyzers.IsEmpty)
            {
                continue;
            }

            report.ReferencesContributing++;
            foreach (var analyzer in projectAnalyzers)
            {
                if (seen.Add(analyzer.GetType().FullName!))
                {
                    builder.Add(analyzer);
                }
            }
        }

        report.AnalyzersLoaded = builder.Count;
        return (builder.ToImmutable(), report);
    }

    /// <summary>
    /// Asks <paramref name="reference"/> for its C# analyzers with
    /// <see cref="AnalyzerFileReference.AnalyzerLoadFailed"/> observed for the duration of the
    /// call. Returns whatever analyzers loaded (possibly none) and, in <paramref name="note"/>,
    /// <see langword="null"/> when the reference contributed everything it carries — otherwise the
    /// note that names what went wrong. A file reference is consulted under a per-reference lock:
    /// Roslyn raises the failure event once and then serves its cached answer silently, so the
    /// observation and its recording in <see cref="_rememberedFailures"/> must not interleave with
    /// another consultation of the same reference (the workspace cache shares references across
    /// calls, and a solution's projects are analyzed in parallel).
    /// </summary>
    private ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers(AnalyzerReference reference, out AnalyzerLoadNote? note)
    {
        if (reference is not AnalyzerFileReference fileReference)
        {
            return LoadAnalyzersCore(reference, fileReference: null, out note);
        }

        lock (_referenceLocks.GetValue(reference, static _ => new object()))
        {
            return LoadAnalyzersCore(reference, fileReference, out note);
        }
    }

    private ImmutableArray<DiagnosticAnalyzer> LoadAnalyzersCore(
        AnalyzerReference reference, AnalyzerFileReference? fileReference, out AnalyzerLoadNote? note)
    {
        AnalyzerLoadFailureEventArgs? failure = null;
        var failures = 0;
        void OnLoadFailed(object? sender, AnalyzerLoadFailureEventArgs e)
        {
            failures++;
            // The first failure is the representative one, except that ReferencesNewerCompiler —
            // raised after every type has failed — is the root cause and displaces it.
            if (failure is null || e.ErrorCode == AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesNewerCompiler)
            {
                failure = e;
            }
        }

        ImmutableArray<DiagnosticAnalyzer> analyzers;
        if (fileReference is not null)
        {
            fileReference.AnalyzerLoadFailed += OnLoadFailed;
        }

        try
        {
            analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load analyzers from {Reference}: {Message}", reference.Display, ex.Message);
            note = new AnalyzerLoadNote
            {
                Reference = reference.Display,
                Reason = AnalyzerLoadNote.Exception,
                Message = ex.Message
            };
            return ImmutableArray<DiagnosticAnalyzer>.Empty;
        }
        finally
        {
            if (fileReference is not null)
            {
                fileReference.AnalyzerLoadFailed -= OnLoadFailed;
            }
        }

        if (failure is not null)
        {
            // Observed on this call: remember it, because Roslyn will not say it again. What
            // loaded is kept — a reference that lost one analyzer type out of forty still ran the
            // other thirty-nine before this report existed, and must go on doing so.
            note = DescribeFailure(reference, failure, failures, analyzers.Length);
            _rememberedFailures.AddOrUpdate(reference, note);
            _logger.LogWarning(
                "Analyzer reference {Reference} could not be fully loaded ({ErrorCode}): {Message}",
                note.Reference, note.ErrorCode, note.Message);
            return analyzers;
        }

        if (_rememberedFailures.TryGetValue(reference, out var remembered))
        {
            // Roslyn already told us, on an earlier call, what this reference could not load; it
            // will not say so again.
            note = remembered;
            return analyzers;
        }

        if (!analyzers.IsEmpty)
        {
            note = null;
            return analyzers;
        }

        _logger.LogDebug("Analyzer reference {Reference} declares no C# analyzers", reference.Display);
        note = new AnalyzerLoadNote { Reference = reference.Display, Reason = AnalyzerLoadNote.NoCSharpAnalyzers };
        return ImmutableArray<DiagnosticAnalyzer>.Empty;
    }

    private static AnalyzerLoadNote DescribeFailure(
        AnalyzerReference reference, AnalyzerLoadFailureEventArgs failure, int failures, int loaded)
    {
        var message = failure.Message;
        if (failure.ReferencedCompilerVersion is { } referenced)
        {
            var inProcess = typeof(Diagnostic).Assembly.GetName().Version;
            message = $"{message} (references Microsoft.CodeAnalysis {referenced}; loaded in-process: {inProcess})";
        }
        else if (failure.Exception is { } exception && !message.Contains(exception.Message, StringComparison.Ordinal))
        {
            message = $"{message}: {exception.Message}";
        }

        if (loaded > 0)
        {
            message = $"partial — {failures} analyzer type(s) could not be loaded, {loaded} loaded: {message}";
        }

        return new AnalyzerLoadNote
        {
            Reference = reference.Display,
            Reason = AnalyzerLoadNote.LoadFailure,
            ErrorCode = failure.ErrorCode.ToString(),
            Message = message
        };
    }

    private sealed class CompilerOnlyComputation : IDiagnosticComputationService
    {
        public Task<DiagnosticComputationResult> GetDiagnosticsAsync(
            Project project,
            Compilation compilation,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DiagnosticComputationResult
            {
                Diagnostics = compilation.GetDiagnostics(cancellationToken)
            });
        }

        public AnalyzerLoadReport DescribeAnalyzerLoad(Project project) => new();
    }
}
