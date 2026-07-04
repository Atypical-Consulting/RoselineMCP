using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using System.Text;

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
    private readonly IMSBuildService _msBuildService;

    /// <summary>
    /// Initializes a new instance of the CodeFixService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="analyzerService">Service for analyzing solutions.</param>
    /// <param name="codeFixProviderFactory">Factory for creating code fix providers.</param>
    /// <param name="diffService">Service for generating diffs.</param>
    /// <param name="msBuildService">Service for MSBuild operations.</param>
    public CodeFixService(
        ILogger<CodeFixService> logger,
        ISolutionAnalyzerService analyzerService,
        ICodeFixProviderFactory codeFixProviderFactory,
        IDiffService diffService,
        IMSBuildService msBuildService)
    {
        _logger = logger;
        _analyzerService = analyzerService;
        _codeFixProviderFactory = codeFixProviderFactory;
        _diffService = diffService;
        _msBuildService = msBuildService;
    }


    /// <inheritdoc/>
    public async Task<ApplyFixesResponse> ApplyFixesAsync(
        string project,
        List<string> ids,
        bool previewOnly = true,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ApplyFixesResponse
        {
            Project = project,
            PreviewOnly = previewOnly
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resolve the project before creating a workspace so a missing project fails fast
            // with FileNotFoundException (classified as NotFoundError at the tool boundary).
            var projectPath = ResolveProjectPath(project);

            // Create a temporary workspace
            using var workspace = _msBuildService.CreateWorkspace();

            workspace.WorkspaceFailed += (sender, e) => _logger.LogWarning("Workspace failed: {Message}", e.Diagnostic.Message);

            // Load the project
            _logger.LogInformation("Loading project for fixes: {Path}", projectPath);
            progress?.Report(new ProgressNotificationValue { Progress = 0, Total = ids.Count, Message = "Loading project via MSBuild…" });

            var msProject = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            response.Project = msProject.Name;

            // Get the original solution text for diff generation
            var originalSolution = workspace.CurrentSolution;
            var originalTexts = new Dictionary<string, string>();

            foreach (var document in msProject.Documents)
            {
                var text = await document.GetTextAsync(cancellationToken);
                originalTexts[document.FilePath!] = text.ToString();
            }

            // Apply fixes for each diagnostic ID
            var appliedFixes = new HashSet<string>();
            var changedDocuments = new HashSet<string>();
            var currentSolution = originalSolution;
            var fixCount = 0;

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

                // Find code fix provider for this diagnostic
                var provider = _codeFixProviderFactory.GetProviderForDiagnostic(diagnosticId);
                if (provider == null)
                {
                    response.Notes.Add($"No code fix provider found for {diagnosticId}");
                    continue;
                }

                try
                {
                    var (updatedSolution, fixedForThisId, anyDiagnosticsFound) =
                        await ApplyFixesForDiagnosticIdAsync(
                            currentSolution, msProject.Id, diagnosticId, provider, changedDocuments, cancellationToken);

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

            // Format the changed documents
            if (changedDocuments.Any())
            {
                _logger.LogInformation("Formatting {Count} changed documents", changedDocuments.Count);

                foreach (var filePath in changedDocuments)
                {
                    var document = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath == filePath);

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

                foreach (var filePath in changedDocuments.OrderBy(f => f))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, filePath);
                    response.ChangedFiles.Add(relativePath);

                    var newDocument = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath == filePath);

                    if (newDocument != null)
                    {
                        var newText = await newDocument.GetTextAsync(cancellationToken);
                        var oldText = originalTexts.GetValueOrDefault(filePath, "");

                        var diff = _diffService.GenerateUnifiedDiff(
                            oldText,
                            newText.ToString(),
                            $"a/{relativePath}",
                            $"b/{relativePath}");

                        if (!string.IsNullOrWhiteSpace(diff))
                        {
                            patchBuilder.AppendLine(diff);
                        }
                    }
                }

                response.Patch = patchBuilder.ToString();
            }

            // Apply changes if not preview only
            if (!previewOnly && changedDocuments.Any())
            {
                _logger.LogInformation("Applying changes to {Count} files", changedDocuments.Count);

                foreach (var filePath in changedDocuments)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var document = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath == filePath);

                    if (document != null)
                    {
                        var text = await document.GetTextAsync(cancellationToken);
                        // Write with the file's original encoding (BOM included) — see SourceTextWriter.
                        await SourceTextWriter.WriteAsync(filePath, text, cancellationToken);
                    }
                }

                response.Notes.Add($"Applied {fixCount} fixes to {changedDocuments.Count} files");
            }
            else if (previewOnly)
            {
                response.Notes.Add("Preview mode - no changes were saved to disk");
            }

            return response;
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
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The resulting solution, how many fixes were applied, and whether any matching diagnostics existed.</returns>
    private async Task<(Solution Solution, int FixedCount, bool AnyDiagnosticsFound)> ApplyFixesForDiagnosticIdAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        CodeFixProvider provider,
        HashSet<string> changedDocuments,
        CancellationToken cancellationToken)
    {
        var initialDiagnostics = await GetMatchingDiagnosticsAsync(solution, projectId, diagnosticId, cancellationToken);
        if (initialDiagnostics.Count == 0) return (solution, 0, false);

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
    /// The diagnostics with the given ID currently reported for the project: unsuppressed and
    /// located in source (the only occurrences a code fix can be applied to).
    /// </summary>
    private static async Task<List<Diagnostic>> GetMatchingDiagnosticsAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId);
        if (project == null) return [];

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null) return [];

        return compilation.GetDiagnostics(cancellationToken)
            .Where(d => d.Id == diagnosticId && !d.IsSuppressed && d.Location.SourceTree != null)
            .ToList();
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
        HashSet<string> changedDocuments,
        CancellationToken cancellationToken)
    {
        var project = solution.GetProject(projectId);
        if (project == null) return (solution, 0);

        var firstDiagnostic = diagnostics
            .OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .First();

        var document = project.Documents.FirstOrDefault(doc =>
            doc.FilePath == firstDiagnostic.Location.SourceTree!.FilePath);
        if (document == null) return (solution, 0);

        var registeredActions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            firstDiagnostic,
            (action, _) => registeredActions.Add(action),
            cancellationToken);

        await provider.RegisterCodeFixesAsync(context);
        if (registeredActions.Count == 0) return (solution, 0);

        var fixAllContext = new FixAllContext(
            document,
            provider,
            FixAllScope.Project,
            registeredActions[0].EquivalenceKey,
            [diagnosticId],
            new PrecomputedDiagnosticProvider(diagnostics),
            cancellationToken);

        var fixAllAction = await fixAllProvider.GetFixAsync(fixAllContext);
        if (fixAllAction == null) return (solution, 0);

        var operations = await fixAllAction.GetOperationsAsync(cancellationToken);
        var operation = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (operation == null) return (solution, 0);

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

        foreach (var projectChanges in newSolution.GetChanges(solution).GetProjectChanges())
        {
            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                var changedDocument = newSolution.GetDocument(documentId);
                if (changedDocument?.FilePath != null)
                {
                    changedDocuments.Add(changedDocument.FilePath);
                }
            }
        }

        _logger.LogDebug("Applied FixAll for {Id}: {Count} occurrence(s) fixed in one pass", diagnosticId, fixedCount);
        return (newSolution, fixedCount);
    }

    /// <summary>
    /// Serves the compiler diagnostics already computed for the project snapshot a
    /// <see cref="FixAllContext"/> is built from, so the batch fixer does not trigger another
    /// full re-analysis. Every served diagnostic is located in source (see
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
        HashSet<string> changedDocuments,
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
            if (project == null) break;

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) break;

            var diagnostic = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Id == diagnosticId && !d.IsSuppressed && d.Location.SourceTree != null)
                .Where(d => !unfixableLocations.Contains((d.Location.SourceTree!.FilePath, d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)))
                .OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Location.SourceSpan.Start)
                .FirstOrDefault();

            if (diagnostic == null) break;

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

            solution = operation.ChangedSolution;
            changedDocuments.Add(document.FilePath!);
            fixedCount++;

            _logger.LogDebug("Applied fix for {Id} in {File}", diagnosticId, document.Name);
        }

        return (solution, fixedCount);
    }

    private string ResolveProjectPath(string project)
    {
        // If it's already a full path to a .csproj file
        if (File.Exists(project) && project.EndsWith(".csproj"))
        {
            return project;
        }

        // If it's a directory, look for .csproj files
        if (Directory.Exists(project))
        {
            var csprojFiles = Directory.GetFiles(project, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                return csprojFiles.First();
            }
        }

        // Try to find in current directory
        var currentDirFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj", SearchOption.AllDirectories);
        var match = currentDirFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(project, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        throw new FileNotFoundException($"Project not found: {project}");
    }

}