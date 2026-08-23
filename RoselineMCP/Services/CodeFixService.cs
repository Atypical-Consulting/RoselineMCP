using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Service for applying automated code fixes to C# projects using Roslyn code fix providers.
/// </summary>
public class CodeFixService : ICodeFixService
{
    private readonly ILogger<CodeFixService> _logger;
    private readonly ISolutionAnalyzerService _analyzerService;
    private readonly ICodeFixProviderFactory _codeFixProviderFactory;
    private readonly IDiffService _diffService;
    private readonly IProjectLoader _projectLoader;
    private readonly IDiagnosticComputationService _diagnosticComputation;
    private readonly IVerificationService _verificationService;

    /// <summary>
    /// Initializes a new instance of the CodeFixService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="analyzerService">Service for analyzing solutions.</param>
    /// <param name="codeFixProviderFactory">Factory for creating code fix providers.</param>
    /// <param name="diffService">Service for generating diffs.</param>
    /// <param name="projectLoader">Loader used to resolve/load the target project.</param>
    /// <param name="verificationService">Compiles the fixed solution in memory and reports what the
    /// fixes did to the compiler's verdict, so a fix that breaks the build is refused before any
    /// file is written.</param>
    /// <param name="diagnosticComputation">Computes compiler + analyzer diagnostics per project,
    /// so analyzer-driven diagnostics (e.g. Roslynator RCS*) are visible to the fixers. When
    /// omitted, falls back to compiler-only diagnostics (production DI always supplies the
    /// analyzer-aware implementation).</param>
    public CodeFixService(
        ILogger<CodeFixService> logger,
        ISolutionAnalyzerService analyzerService,
        ICodeFixProviderFactory codeFixProviderFactory,
        IDiffService diffService,
        IProjectLoader projectLoader,
        IVerificationService verificationService,
        IDiagnosticComputationService? diagnosticComputation = null)
    {
        _logger = logger;
        _analyzerService = analyzerService;
        _codeFixProviderFactory = codeFixProviderFactory;
        _diffService = diffService;
        _projectLoader = projectLoader;
        _verificationService = verificationService;
        _diagnosticComputation = diagnosticComputation ?? DiagnosticComputationService.CompilerOnly;
    }


    /// <inheritdoc/>
    public async Task<ApplyFixesResponse> ApplyFixesAsync(
        string? project,
        List<string> ids,
        bool previewOnly = true,
        bool allowIntroducedErrors = false,
        int max = 20,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ApplyFixesResponse
        {
            Project = project ?? string.Empty,
            PreviewOnly = previewOnly
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Load through IProjectLoader — the same resolution the navigation/edit tools use
            // (auto-discovery when 'project' is omitted, .sln paths accepted, exact-name project
            // selection). A missing project still fails fast with FileNotFoundException
            // (classified as NotFoundError at the tool boundary) before any workspace is opened.
            // The caching loader's on-disk fingerprint self-invalidates after this tool's writes.
            _logger.LogInformation("Loading project for fixes: {Project}", project ?? "(auto-discovered)");
            progress?.Report(new ProgressNotificationValue { Progress = 0, Total = ids.Count, Message = "Loading project via MSBuild…" });

            using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
            try
            {
                var msProject = loaded.Project;
                response.Project = msProject.Name;
                response.ResolvedPath = loaded.ResolvedPath;

                // Say which project this call fixes and which it leaves alone, on every path out of
                // here. The confirmation prompt names the scope too, but previewOnly (the default), a
                // client without elicitation, and ConfirmDestructiveWrites = false all skip the prompt —
                // and a short changedFiles is not evidence that the other projects were clean (#156).
                var skippedNote = DescribeSkippedProjects(loaded, msProject);
                if (skippedNote is not null)
                {
                    response.Notes.Add(skippedNote);
                }

                // Emitted paths are solution-root-relative (falling back to the project directory when
                // no .sln was loaded), matching the navigation and edit tools.
                var baseDirectory = Path.GetDirectoryName(loaded.Solution.FilePath ?? msProject.FilePath);

                // Get the original solution text for diff generation
                var originalSolution = loaded.Solution;
                var originalTexts = new Dictionary<string, string>();

                foreach (var document in msProject.Documents)
                {
                    var text = await document.GetTextAsync(cancellationToken);
                    originalTexts[document.FilePath!] = text.ToString();
                }

                // Apply fixes for each diagnostic ID. changedDocuments holds the ANCHOR project's
                // document ids — never paths. A linked file is one path backing one document per
                // project that links it, so a path cannot say which copy carries the fix; the id can
                // (#156). Both collectors below fill it from the solution delta, anchor-only.
                var appliedFixes = new HashSet<string>();
                var changedDocuments = new HashSet<DocumentId>();
                var currentSolution = originalSolution;
                var fixCount = 0;
                var analyzerLoad = new AnalyzerLoadCapture();

                var diagnosticIndex = 0;
                foreach (var diagnosticId in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    diagnosticIndex++;
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = diagnosticIndex,
                        Total = ids.Count,
                        Message = $"Fixing {diagnosticId} ({diagnosticIndex}/{ids.Count})"
                    });
                    _logger.LogInformation("Attempting to fix diagnostic: {Id}", diagnosticId);

                    // Find code fix provider for this diagnostic — process-wide first, then the
                    // providers the project's own analyzer references carry.
                    var provider = _codeFixProviderFactory.GetProviderForDiagnostic(diagnosticId, msProject);
                    if (provider == null)
                    {
                        response.Notes.Add($"No code fix provider found for {diagnosticId}");
                        continue;
                    }

                    try
                    {
                        var (updatedSolution, fixedForThisId, anyDiagnosticsFound) =
                            await ApplyFixesForDiagnosticIdAsync(
                                currentSolution, msProject.Id, diagnosticId, provider, changedDocuments, analyzerLoad, cancellationToken);

                        currentSolution = updatedSolution;

                        if (fixedForThisId > 0)
                        {
                            fixCount += fixedForThisId;
                            appliedFixes.Add(diagnosticId);
                        }
                        else if (!anyDiagnosticsFound)
                        {
                            response.Notes.Add($"No diagnostics found for {diagnosticId}");
                        }
                        else
                        {
                            response.Notes.Add($"No code fix could be applied for {diagnosticId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error applying fixes for diagnostic {Id}", diagnosticId);
                        response.Notes.Add($"Error fixing {diagnosticId}: {ex.Message}");
                    }
                }

                response.FixersApplied = appliedFixes.ToList();
                response.FixedCount = fixCount;
                // When no requested ID had a fixer, no diagnostics pass ran and nothing was captured —
                // yet that is the headline case to explain: the reference carrying both the analyzer
                // and its fixer may be the one that failed to load. Describe the load anyway.
                response.AnalyzerLoad = AnalyzerLoadReport.ForResponse(
                    analyzerLoad.Report ?? _diagnosticComputation.DescribeAnalyzerLoad(msProject));

                // Format the changed documents
                if (changedDocuments.Any())
                {
                    _logger.LogInformation("Formatting {Count} changed documents", changedDocuments.Count);

                    foreach (var documentId in changedDocuments)
                    {
                        var document = currentSolution.GetDocument(documentId);

                        if (document != null)
                        {
                            document = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
                            currentSolution = document.Project.Solution;
                        }
                    }
                }

                // Generate patch
                if (changedDocuments.Any())
                {
                    var patchBuilder = new StringBuilder();

                    // A linked file is the anchor's document AND a sibling's: writing it is in scope,
                    // its effect on the sibling is not. Collected per file here, said once per
                    // sibling set below, so a fix across a linked Shared/ folder reads as one note.
                    var linkedFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                    foreach (var newDocument in ChangedDocumentsByPath(currentSolution, changedDocuments))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var filePath = newDocument.FilePath!;
                        var relativePath = SymbolResolver.Relativize(filePath, baseDirectory) ?? filePath;

                        var newText = await newDocument.GetTextAsync(cancellationToken);
                        var oldText = originalTexts.GetValueOrDefault(filePath, "");

                        var diff = _diffService.GenerateUnifiedDiff(
                            oldText,
                            newText.ToString(),
                            $"a/{relativePath}",
                            $"b/{relativePath}");

                        // Roslyn's changed-document set is a "this document object was touched"
                        // signal, not a content-equality check — a fixer whose edit nets out to the
                        // original text (or is undone by the formatting pass above) still shows up
                        // in changedDocuments. Only count it as changed once there is a real, non-blank
                        // diff, so HasChanges agrees with what ApplyFixes actually has to write.
                        if (!string.IsNullOrWhiteSpace(diff))
                        {
                            patchBuilder.AppendLine(diff);
                            response.ChangedFiles.Add(relativePath);

                            var sharers = OtherProjectNames(
                                currentSolution.GetDocumentIdsWithFilePath(filePath).Select(id => currentSolution.GetProject(id.ProjectId)),
                                msProject);
                            if (sharers.Count > 0)
                            {
                                var key = string.Join(", ", sharers);
                                if (!linkedFiles.TryGetValue(key, out var files))
                                {
                                    linkedFiles[key] = files = [];
                                }

                                files.Add(relativePath);
                            }
                        }
                    }

                    response.Patch = patchBuilder.ToString();

                    foreach (var (sharers, files) in linkedFiles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        var plural = files.Count == 1 ? "is a linked file" : "are linked files";
                        response.Notes.Add(
                            $"{string.Join(", ", files.Select(f => $"'{f}'"))} {plural} also compiled by {sharers}: "
                            + $"writing changes what those projects compile too, though only '{ProjectDisplayName(msProject)}' was analyzed.");
                    }
                }

                // The compiler's verdict on the fixed solution, before anything reaches disk. A code fix
                // is generated code the caller never wrote, so this is the one assurance worth checking
                // rather than trusting — and it runs ahead of the tool's write confirmation, so a human
                // is never asked to approve a fix that is about to be refused.
                if (changedDocuments.Any())
                {
                    var verdict = await _verificationService.VerifyAsync(
                        originalSolution, currentSolution, max, cancellationToken);
                    response.Verification = verdict;

                    if (verdict.Introduced is { Count: > 0 } introduced && !allowIntroducedErrors)
                    {
                        _logger.LogInformation(
                            "Refused {Count} fix(es): they introduce {Errors} compiler error(s)",
                            fixCount, introduced.Count);
                        var omitted = verdict.Omitted > 0 ? $" (+{verdict.Omitted} more not shown)" : string.Empty;
                        response.Notes.Add(
                            $"Refused: these fixes introduce {introduced.Count} compiler error(s){omitted} — nothing was "
                            + "written. Fix the change, or pass allowIntroducedErrors: true to write it anyway.");
                        return response;
                    }
                }

                // Apply changes if not preview only
                if (!previewOnly && changedDocuments.Any())
                {
                    _logger.LogInformation("Applying changes to {Count} files", changedDocuments.Count);

                    foreach (var document in ChangedDocumentsByPath(currentSolution, changedDocuments))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var text = await document.GetTextAsync(cancellationToken);
                        // Write with the file's original encoding (BOM included) — see SourceTextWriter.
                        await SourceTextWriter.WriteAsync(document.FilePath!, text, cancellationToken);
                    }

                    response.Applied = true;
                    response.Notes.Add($"Applied {fixCount} fixes to {changedDocuments.Count} files");
                }
                else if (previewOnly)
                {
                    response.Notes.Add("Preview mode - no changes were saved to disk");
                }

                return response;
            }
            catch (Exception ex)
            {
                // Name the checkout that answered, so the failure envelope can tell
                // "the symbol is not here" apart from "you asked the wrong checkout".
                ResolvedPathStamp.Stamp(ex, loaded);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            // Log for diagnosability, then let cancellation (caller-initiated or the
            // DefaultTimeout linked token) propagate — the MCP tool boundary (ApplyFixesTool)
            // has a dedicated catch for this and reports it as a Cancelled/Timeout error.
            _logger.LogWarning("Apply fixes operation was cancelled");
            throw;
        }
        // Any other exception (missing project, MSBuild load failure, ...) propagates to the
        // MCP tool boundary, where ToolExecutionHelper.Error classifies it into the documented
        // closed error-type set and returns the { ok: false, error: ... } envelope. Folding such
        // failures into a normal-looking response here would make the tool report ok: true for
        // an operation that actually failed. Per-diagnostic-ID fixer errors are still handled
        // gracefully above (as Notes entries) so one broken fixer doesn't abort the whole run.
    }

    /// <summary>
    /// Applies fixes for a single diagnostic ID. When the provider ships a
    /// <see cref="FixAllProvider"/> that supports <see cref="FixAllScope.Project"/>, all
    /// occurrences are fixed in one batch pass (<see cref="TryApplyFixAllAsync"/>) — a single
    /// compilation instead of one full re-analysis per occurrence. Any occurrences the batch
    /// pass did not cover (or every occurrence, for providers without FixAll support) are then
    /// handled by the per-occurrence fallback (<see cref="ApplyFixesOccurrenceByOccurrenceAsync"/>).
    /// </summary>
    /// <param name="solution">The solution to start from.</param>
    /// <param name="projectId">The ID of the project being fixed.</param>
    /// <param name="diagnosticId">The diagnostic ID to fix.</param>
    /// <param name="provider">The code fix provider to use.</param>
    /// <param name="changedDocuments">Accumulator of file paths that were modified.</param>
    /// <param name="analyzerLoad">Receives the analyzer-load report of the first diagnostics pass.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The resulting solution, how many fixes were applied, and whether any matching diagnostics existed.</returns>
    private async Task<(Solution Solution, int FixedCount, bool AnyDiagnosticsFound)> ApplyFixesForDiagnosticIdAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        CodeFixProvider provider,
        HashSet<DocumentId> changedDocuments,
        AnalyzerLoadCapture analyzerLoad,
        CancellationToken cancellationToken)
    {
        var initialDiagnostics = await GetMatchingDiagnosticsAsync(solution, projectId, diagnosticId, cancellationToken, analyzerLoad);
        if (initialDiagnostics.Count == 0)
        {
            return (solution, 0, false);
        }

        var totalFixed = 0;
        var remaining = initialDiagnostics.Count;

        var fixAllProvider = provider.GetFixAllProvider();
        if (fixAllProvider != null && fixAllProvider.GetSupportedFixAllScopes().Contains(FixAllScope.Project))
        {
            var (fixAllSolution, fixedByFixAll) = await TryApplyFixAllAsync(
                solution, projectId, diagnosticId, provider, fixAllProvider, initialDiagnostics, changedDocuments, cancellationToken);

            if (fixedByFixAll > 0)
            {
                solution = fixAllSolution;
                totalFixed += fixedByFixAll;
                remaining -= fixedByFixAll;
            }
        }

        if (remaining > 0)
        {
            var (loopSolution, fixedByLoop) = await ApplyFixesOccurrenceByOccurrenceAsync(
                solution, projectId, diagnosticId, provider, changedDocuments, remaining, cancellationToken);
            solution = loopSolution;
            totalFixed += fixedByLoop;
        }

        return (solution, totalFixed, true);
    }

    /// <summary>
    /// The diagnostics with the given ID currently reported for the project — compiler and
    /// analyzer diagnostics alike (see <see cref="IDiagnosticComputationService"/>):
    /// unsuppressed and located in source (the only occurrences a code fix can be applied to).
    /// When <paramref name="analyzerLoad"/> is given, the pass's analyzer-load report is handed to
    /// it — the response carries the first one, so a caller can tell "no diagnostics found" from
    /// "the analyzer that reports them never loaded".
    /// </summary>
    private async Task<List<Diagnostic>> GetMatchingDiagnosticsAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        CancellationToken cancellationToken,
        AnalyzerLoadCapture? analyzerLoad = null)
    {
        var project = solution.GetProject(projectId);
        if (project == null)
        {
            return [];
        }

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            return [];
        }

        var computed = await _diagnosticComputation.GetDiagnosticsAsync(project, compilation, cancellationToken);
        analyzerLoad?.Record(computed.AnalyzerLoad);
        return computed.Diagnostics
            .Where(d => d.Id == diagnosticId && !d.IsSuppressed && d.Location.SourceTree != null)
            .ToList();
    }

    /// <summary>
    /// Keeps the analyzer-load report of the <em>first</em> diagnostics pass of an
    /// <c>apply_fixes</c> call. Every later pass in the same call consults the same references
    /// and would say the same thing.
    /// </summary>
    private sealed class AnalyzerLoadCapture
    {
        public AnalyzerLoadReport? Report { get; private set; }

        public void Record(AnalyzerLoadReport report) => Report ??= report;
    }

    /// <summary>
    /// Fixes every occurrence of <paramref name="diagnosticId"/> in one batch pass through the
    /// provider's own <see cref="FixAllProvider"/>, instead of re-compiling the project after
    /// each individual fix. The equivalence key that selects which code action to batch is taken
    /// from the first registered action of the first occurrence — the same action the
    /// per-occurrence path would apply. Returns the original solution and a count of 0 whenever
    /// the batch pass cannot be used or fixed nothing (no registered action, no fix-all action,
    /// no <see cref="ApplyChangesOperation"/>, or no diagnostic actually disappeared), leaving
    /// the per-occurrence fallback to handle everything from the unchanged solution. The fixed
    /// count is computed by re-counting matching diagnostics after the pass, so occurrences the
    /// batch could not fix are reported accurately and handed to the fallback.
    /// </summary>
    private async Task<(Solution Solution, int FixedCount)> TryApplyFixAllAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        CodeFixProvider provider,
        FixAllProvider fixAllProvider,
        List<Diagnostic> diagnostics,
        HashSet<DocumentId> changedDocuments,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId);
        if (project == null)
        {
            return (solution, 0);
        }

        var firstDiagnostic = diagnostics
            .OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .First();

        var document = project.Documents.FirstOrDefault(doc =>
            doc.FilePath == firstDiagnostic.Location.SourceTree!.FilePath);
        if (document == null)
        {
            return (solution, 0);
        }

        var registeredActions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            firstDiagnostic,
            (action, _) => registeredActions.Add(action),
            cancellationToken);

        await provider.RegisterCodeFixesAsync(context);
        if (registeredActions.Count == 0)
        {
            return (solution, 0);
        }

        var fixAllContext = new FixAllContext(
            document,
            provider,
            FixAllScope.Project,
            registeredActions[0].EquivalenceKey,
            [diagnosticId],
            new PrecomputedDiagnosticProvider(diagnostics),
            cancellationToken);

        var fixAllAction = await fixAllProvider.GetFixAsync(fixAllContext);
        if (fixAllAction == null)
        {
            return (solution, 0);
        }

        var operations = await fixAllAction.GetOperationsAsync(cancellationToken);
        var operation = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (operation == null)
        {
            return (solution, 0);
        }

        var newSolution = operation.ChangedSolution;

        // FixedCount is the number of occurrences that actually disappeared, so the response
        // contract stays identical to the per-occurrence path.
        var remainingDiagnostics = await GetMatchingDiagnosticsAsync(newSolution, projectId, diagnosticId, cancellationToken);
        var fixedCount = diagnostics.Count - remainingDiagnostics.Count;
        if (fixedCount <= 0)
        {
            // The batch pass fixed nothing — discard it and let the per-occurrence fallback
            // work from the unchanged solution.
            return (solution, 0);
        }

        // Keep ONLY the anchor project's changes. The FixAllContext above is scoped to the project,
        // but a FixAllProvider is third-party code and nothing stops one from touching documents
        // elsewhere in the solution. What comes back here is both what gets verified and what gets
        // written, so the two must be the same set: rebuilding from `solution` plus the anchor's
        // changed documents makes that true by construction, instead of filtering the write list
        // while verifying a solution that still carries the sibling edits (#156).
        var (scopedSolution, changedIds) = await KeepAnchorChangesAsync(solution, newSolution, projectId, cancellationToken);
        changedDocuments.UnionWith(changedIds);
        newSolution = scopedSolution;

        _logger.LogDebug("Applied FixAll for {Id}: {Count} occurrence(s) fixed in one pass", diagnosticId, fixedCount);
        return (newSolution, fixedCount);
    }

    /// <summary>
    /// <paramref name="before"/> plus only the <paramref name="anchor"/> project's document changes
    /// from <paramref name="after"/> — the solution every later step works from, and the ids of the
    /// documents it differs in. Defined once, for both the batch and the per-occurrence path, from
    /// the solution delta rather than from whichever document a fix was registered on.
    /// </summary>
    /// <remarks>
    /// Changed documents are carried over as text; documents a fix <em>added</em> to the anchor are
    /// carried over too, when they have a path to be written to. Anything a fixer did outside the
    /// anchor is dropped here — and because the returned solution is what the compiler verifies and
    /// what the write loop reads, "verified" and "written" cannot disagree about it.
    /// </remarks>
    private static async Task<(Solution Solution, List<DocumentId> Changed)> KeepAnchorChangesAsync(
        Solution before,
        Solution after,
        ProjectId anchor,
        CancellationToken cancellationToken)
    {
        var result = before;
        var changed = new List<DocumentId>();

        foreach (var projectChanges in after.GetChanges(before).GetProjectChanges()
                     .Where(pc => pc.ProjectId == anchor))
        {
            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                var document = after.GetDocument(documentId);
                if (document?.FilePath is null)
                {
                    continue;
                }

                result = result.WithDocumentText(documentId, await document.GetTextAsync(cancellationToken));
                changed.Add(documentId);
            }

            foreach (var documentId in projectChanges.GetAddedDocuments())
            {
                var document = after.GetDocument(documentId);
                if (document?.FilePath is null)
                {
                    continue;
                }

                result = result.AddDocument(
                    documentId, document.Name, await document.GetTextAsync(cancellationToken), document.Folders, document.FilePath);
                changed.Add(documentId);
            }
        }

        return (result, changed);
    }

    /// <summary>
    /// The documents behind <paramref name="changedDocuments"/> in <paramref name="solution"/>, in
    /// file-path order — the one iteration order the patch and the write loop share.
    /// </summary>
    private static IEnumerable<Document> ChangedDocumentsByPath(Solution solution, IEnumerable<DocumentId> changedDocuments) =>
        changedDocuments
            .Select(solution.GetDocument)
            .Where(d => d?.FilePath is not null)
            .Select(d => d!)
            .OrderBy(d => d.FilePath, StringComparer.Ordinal);

    /// <summary>
    /// The scope note for a run anchored on <paramref name="anchor"/>: which project was fixed and
    /// which other C# projects of the loaded solution were not analyzed or fixed. <c>null</c> when
    /// there is nothing to say — the caller named a project (a <c>.csproj</c> path or name, even
    /// one whose ancestor <c>.sln</c> was opened to answer), no solution was loaded, or the solution
    /// holds no other project — because "0 other projects" is noise, and telling a caller who asked
    /// for one project to pass its <c>.csproj</c> is worse than noise.
    /// </summary>
    private static string? DescribeSkippedProjects(LoadedProject loaded, Project anchor)
    {
        if (!loaded.TargetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var skipped = OtherProjectNames(loaded.Solution.Projects, anchor);
        if (skipped.Count == 0)
        {
            return null;
        }

        var solutionName = Path.GetFileName(loaded.TargetPath);
        var plural = skipped.Count == 1 ? "project" : "projects";
        return $"Fixed project '{ProjectDisplayName(anchor)}' only; {skipped.Count} other {plural} in '{solutionName}' "
               + $"({string.Join(", ", skipped)}) were not analyzed or fixed. "
               + "Pass a project's .csproj as 'project' to fix it.";
    }

    /// <summary>
    /// The C# projects among <paramref name="projects"/> that are not <paramref name="anchor"/>'s
    /// <c>.csproj</c>, named once each and sorted. Keyed on the project <em>file</em>, not the
    /// Roslyn project: a multi-targeted project loads as one Roslyn project per TFM
    /// (<c>Lib(net8.0)</c>, <c>Lib(net10.0)</c>) over one <c>.csproj</c>, and that is one project
    /// to skip, or to share a linked file with — and the anchor's own other legs are not "other
    /// projects" at all, since the write covers them.
    /// </summary>
    private static List<string> OtherProjectNames(IEnumerable<Project?> projects, Project anchor) =>
        projects
            .Where(p => p is { Language: LanguageNames.CSharp, FilePath: not null }
                        && !string.Equals(p.FilePath, anchor.FilePath, StringComparison.Ordinal))
            .Select(p => ProjectDisplayName(p!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>The project's <c>.csproj</c> file name, so every TFM leg of one project reads as that project.</summary>
    private static string ProjectDisplayName(Project project) =>
        project.FilePath is null ? project.Name : Path.GetFileNameWithoutExtension(project.FilePath);

    /// <summary>
    /// Serves the diagnostics (compiler and analyzer alike) already computed for the project
    /// snapshot a <see cref="FixAllContext"/> is built from, so the batch fixer does not trigger
    /// another full re-analysis. Every served diagnostic is located in source (see
    /// <see cref="GetMatchingDiagnosticsAsync"/>), so there are no project-level diagnostics.
    /// </summary>
    private sealed class PrecomputedDiagnosticProvider(List<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(
                diagnostics.Where(d => d.Location.SourceTree?.FilePath == document.FilePath).ToList());

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());
    }

    /// <summary>
    /// Applies fixes for a single diagnostic ID, one occurrence at a time, re-analyzing the
    /// solution after every applied fix so that later occurrences see up-to-date source text.
    /// This is the fallback for providers without a Project-scope <see cref="FixAllProvider"/>
    /// (and the mop-up for occurrences a batch pass did not cover — see
    /// <see cref="TryApplyFixAllAsync"/>).
    /// This intentionally avoids any concurrency: a prior fix can shift line/column offsets
    /// for other diagnostics in the same document, so each <see cref="CodeFixContext"/> is
    /// built from a freshly recomputed diagnostic against the latest solution snapshot, and
    /// its operations are awaited to completion before the next occurrence is even looked up.
    /// Only the FIRST <see cref="CodeAction"/> a provider registers for a given occurrence is
    /// applied — a provider that offers several candidate fixes for the same diagnostic (e.g.
    /// an ambiguous-reference fix with multiple candidate namespaces) must not have every one
    /// of them applied sequentially, since each is computed from the same pre-fix snapshot and
    /// later ones would silently overwrite earlier ones while still being counted as separate
    /// successful fixes. An occurrence that yields no usable code action (no registered actions,
    /// or none that produce an <see cref="ApplyChangesOperation"/>) is skipped — not treated as
    /// a reason to abort every other occurrence of the same diagnostic ID — so it is remembered
    /// and excluded from subsequent occurrence selection to guarantee forward progress.
    /// </summary>
    /// <param name="solution">The solution to start from.</param>
    /// <param name="projectId">The ID of the project being fixed.</param>
    /// <param name="diagnosticId">The diagnostic ID to fix.</param>
    /// <param name="provider">The code fix provider to use.</param>
    /// <param name="changedDocuments">Accumulator of file paths that were modified.</param>
    /// <param name="remainingCount">How many matching occurrences are still expected to exist.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The resulting solution and how many fixes were applied.</returns>
    private async Task<(Solution Solution, int FixedCount)> ApplyFixesOccurrenceByOccurrenceAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        CodeFixProvider provider,
        HashSet<DocumentId> changedDocuments,
        int remainingCount,
        CancellationToken cancellationToken)
    {
        // Bound the number of re-analysis passes so a fixer that keeps registering a
        // no-op/ineffective action for the same occurrence can't loop forever.
        var maxIterations = remainingCount + 5;
        var fixedCount = 0;

        // Occurrences that turned out to be unfixable (no usable code action) are remembered
        // here, keyed by their source location, so subsequent iterations skip them and move on
        // to other occurrences instead of endlessly reselecting the same unfixable one.
        var unfixableLocations = new HashSet<(string FilePath, int Start, int Length)>();

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var project = solution.GetProject(projectId);
            if (project == null)
            {
                break;
            }

            // Compiler + analyzer diagnostics, recomputed against the latest snapshot.
            var matchingDiagnostics = await GetMatchingDiagnosticsAsync(solution, projectId, diagnosticId, cancellationToken);
            var diagnostic = matchingDiagnostics
                .Where(d => !unfixableLocations.Contains((d.Location.SourceTree!.FilePath, d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)))
                .OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Location.SourceSpan.Start)
                .FirstOrDefault();

            if (diagnostic == null)
            {
                break;
            }

            var locationKey = (diagnostic.Location.SourceTree!.FilePath, diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

            var document = project.Documents.FirstOrDefault(doc =>
                doc.FilePath == diagnostic.Location.SourceTree!.FilePath);
            if (document == null)
            {
                unfixableLocations.Add(locationKey);
                continue;
            }

            // Synchronous registration delegate — no async void. CodeActions are queued here
            // and their operations are explicitly awaited below, after registration completes.
            var registeredActions = new List<CodeAction>();
            var context = new CodeFixContext(
                document,
                diagnostic,
                (action, _) => registeredActions.Add(action),
                cancellationToken);

            await provider.RegisterCodeFixesAsync(context);

            if (registeredActions.Count == 0)
            {
                // This occurrence has no registered fix — skip only it, other occurrences of
                // the same diagnostic ID (e.g. in a different file) may still be fixable.
                unfixableLocations.Add(locationKey);
                continue;
            }

            // Apply only the first registered CodeAction for this occurrence — applying every
            // registered action would apply multiple competing fixes for what is really a
            // single diagnostic occurrence.
            var operations = await registeredActions[0].GetOperationsAsync(cancellationToken);
            var operation = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (operation == null)
            {
                unfixableLocations.Add(locationKey);
                continue;
            }

            // Same collector as the FixAll path: the solution delta, anchor project only — NOT the
            // document the fix was registered on, which misses every other document a
            // multi-document fix edits or adds (and a fix that edited only a sibling would be
            // counted as applied while writing nothing).
            var (scopedSolution, changedIds) = await KeepAnchorChangesAsync(solution, operation.ChangedSolution, projectId, cancellationToken);
            if (changedIds.Count == 0)
            {
                unfixableLocations.Add(locationKey);
                continue;
            }

            solution = scopedSolution;
            changedDocuments.UnionWith(changedIds);
            fixedCount++;

            _logger.LogDebug("Applied fix for {Id} in {File}", diagnosticId, document.Name);
        }

        return (solution, fixedCount);
    }

}
