using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Roslyn-backed implementation of <see cref="IVerificationService"/>: compiles a candidate solution
/// in memory and reports whether it compiles and what the change did to the compiler's verdict.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> A delta verification compiles the projects the change touched plus their
/// <em>transitive dependents</em> — the set computed from
/// <see cref="Solution.GetProjectDependencyGraph"/>. File-only scope misses the cross-project
/// breakage agents fail at most; whole-solution scope pays to compile projects the change cannot
/// possibly affect. An absolute verification (no baseline) has no changed set, so its scope is the
/// whole solution.
/// </para>
/// <para>
/// <b>Diagnostics.</b> The pass is delegated to <see cref="IDiagnosticComputationService"/> —
/// production wiring supplies <see cref="DiagnosticComputationService.CompilerOnly"/> rather than
/// the analyzer-aware implementation. That is the design, not an oversight: analyzers cost several
/// times a bare compile and would turn a build gate into a style gate.
/// </para>
/// </remarks>
public class VerificationService : IVerificationService, IDisposable
{
    /// <summary>Maximum number of cached per-project diagnostic sets before the least-recently-used one is evicted.</summary>
    internal const int MaxCacheEntries = 16;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ILogger<VerificationService> _logger;
    private readonly IDiagnosticComputationService _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new(CacheKeyComparer.Instance);
    private long _accessCounter;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="VerificationService"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="diagnostics">
    /// The diagnostics pass. Pass <see cref="DiagnosticComputationService.CompilerOnly"/> — this is
    /// a compilation gate, not a style gate.
    /// </param>
    public VerificationService(
        ILogger<VerificationService> logger,
        IDiagnosticComputationService diagnostics)
    {
        _logger = logger;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc/>
    public async Task<VerificationVerdict> VerifyAsync(
        Solution? baseline,
        Solution candidate,
        int max = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scope = ComputeScope(baseline, candidate);
        var notes = new List<string>();

        // A bare .csproj was loaded with no containing solution, so the workspace holds no dependents
        // to compile — a public-signature change is safe *within* this project and may break every
        // consumer of it. Saying so is the difference between a partial gate and a false green: the
        // write still proceeds, but the caller knows what was checked.
        var scopeComplete = candidate.FilePath is not null;
        if (!scopeComplete)
        {
            notes.Add(
                "Scope is incomplete: no containing solution was loaded, so projects that depend on "
                + "this one were not compiled. Pass the .sln path as `project` to verify them too.");
        }

        // A file can back documents in more than one project — a multi-targeted project loads as one
        // Roslyn project per TFM over the same paths, and a linked <Compile Include="../Shared.cs" />
        // does the same across projects. The candidate only changes the ONE DocumentId that was
        // edited, but the write changes the file for every project that includes it. The sibling
        // legs therefore keep their pre-edit text, never enter the scope, and an edit that breaks
        // the `#if NET8_0` half would come back `introduced: []` with `compiles: true`.
        //
        // That is the exact false green scopeComplete exists to prevent, so it must say so here too.
        var unseen = ProjectsSharingChangedFiles(baseline, candidate, scope);
        if (unseen.Count > 0)
        {
            scopeComplete = false;
            notes.Add(
                "Scope is incomplete: a changed file also belongs to "
                + string.Join(", ", unseen.OrderBy(n => n, StringComparer.Ordinal))
                + " (multi-targeting or a linked file), which the write will change but this "
                + "verification did not compile.");
        }

        var verdict = new VerificationVerdict
        {
            Scope = scope.Select(id => candidate.GetProject(id)?.Name ?? id.ToString())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList(),
            ScopeComplete = scopeComplete,
            Notes = notes.Count > 0 ? notes : null
        };

        var candidateErrors = await CollectErrorsAsync(candidate, scope, cancellationToken);
        verdict.Compiles = candidateErrors.Count == 0;

        if (baseline is null)
        {
            verdict.Errors = Truncate(candidateErrors, max, out var omitted);
            verdict.Omitted = omitted;
            return verdict;
        }

        var baselineErrors = await CollectErrorsAsync(baseline, scope, cancellationToken);
        var (introduced, resolved) = Delta(baselineErrors, candidateErrors);

        verdict.Introduced = Truncate(introduced, max, out var introducedOmitted);
        verdict.Resolved = Truncate(resolved, max, out var resolvedOmitted);
        verdict.Omitted = introducedOmitted + resolvedOmitted;
        verdict.Preexisting = candidateErrors.Count - introduced.Count;
        return verdict;
    }

    /// <summary>
    /// The position-insensitive matching key: project, file, diagnostic id, message. Line and column
    /// are deliberately excluded — they are retained in the payload but must never decide identity.
    /// A pre-existing <c>CS0103</c> at line 80 that a three-line edit above pushes to line 83 is the
    /// same error; a key that carried its position would call it introduced <em>and</em> the original
    /// resolved, and refuse a write for a break the edit never made.
    /// </summary>
    private static (string Project, string File, string Id, string Message) KeyOf(DiagnosticDetail detail) =>
        (detail.Project, detail.File, detail.Id, detail.Message);

    /// <summary>
    /// A <b>multiset</b> difference over <see cref="KeyOf"/>: an edit that genuinely adds a second
    /// identical error in the same file must report one introduced error, which plain set semantics
    /// would silently swallow.
    /// </summary>
    private static (List<DiagnosticDetail> Introduced, List<DiagnosticDetail> Resolved) Delta(
        List<DiagnosticDetail> baselineErrors,
        List<DiagnosticDetail> candidateErrors)
    {
        var unmatched = new Dictionary<(string, string, string, string), List<DiagnosticDetail>>();
        foreach (var error in baselineErrors)
        {
            var key = KeyOf(error);
            if (!unmatched.TryGetValue(key, out var bucket))
            {
                bucket = [];
                unmatched[key] = bucket;
            }

            bucket.Add(error);
        }

        var introduced = new List<DiagnosticDetail>();
        foreach (var error in candidateErrors)
        {
            if (unmatched.TryGetValue(KeyOf(error), out var bucket) && bucket.Count > 0)
            {
                // Matched against a baseline occurrence: pre-existing, not the caller's doing.
                bucket.RemoveAt(bucket.Count - 1);
            }
            else
            {
                introduced.Add(error);
            }
        }

        var resolved = unmatched.Values.SelectMany(bucket => bucket).ToList();
        return (introduced, resolved);
    }

    /// <summary>
    /// The projects to compile: the changed projects plus everything that transitively depends on
    /// them, or the whole solution when there is no baseline to diff against.
    /// </summary>
    private static IReadOnlyList<ProjectId> ComputeScope(Solution? baseline, Solution candidate)
    {
        if (baseline is null)
        {
            return candidate.ProjectIds;
        }

        // Derived here, never accepted from the caller: a caller that under-reports its changed set
        // would silently narrow the scope and let the gate pass broken code.
        var changed = candidate.GetChanges(baseline).GetProjectChanges()
            .Select(change => change.ProjectId)
            .ToHashSet();

        // Projects the candidate added outright are changes too, and GetProjectChanges() does not
        // report them.
        foreach (var addedId in candidate.ProjectIds.Where(id => baseline.GetProject(id) is null))
        {
            changed.Add(addedId);
        }

        if (changed.Count == 0)
        {
            return [];
        }

        var graph = candidate.GetProjectDependencyGraph();
        var scope = new HashSet<ProjectId>(changed);
        foreach (var projectId in changed)
        {
            foreach (var dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(projectId))
            {
                scope.Add(dependent);
            }
        }

        return [.. scope];
    }

    /// <summary>
    /// Names the projects that hold a document backed by one of the changed <em>files</em> but are
    /// not in the compiled scope — the multi-targeting / linked-file case. Empty in absolute mode,
    /// which has no changed set.
    /// </summary>
    private static IReadOnlyCollection<string> ProjectsSharingChangedFiles(
        Solution? baseline,
        Solution candidate,
        IReadOnlyList<ProjectId> scope)
    {
        if (baseline is null)
        {
            return [];
        }

        var inScope = scope.ToHashSet();
        var unseen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var projectChange in candidate.GetChanges(baseline).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var filePath = candidate.GetDocument(documentId)?.FilePath;
                if (string.IsNullOrEmpty(filePath))
                {
                    continue;
                }

                // Roslyn indexes documents by path, so this is a lookup rather than a scan.
                foreach (var sharedId in candidate.GetDocumentIdsWithFilePath(filePath))
                {
                    if (!inScope.Contains(sharedId.ProjectId))
                    {
                        unseen.Add(candidate.GetProject(sharedId.ProjectId)?.Name ?? sharedId.ProjectId.ToString());
                    }
                }
            }
        }

        return unseen;
    }

    /// <summary>
    /// Compiles every project in scope and projects its compiler <b>errors</b> (warnings are not a
    /// build gate) into the wire model.
    /// </summary>
    private async Task<List<DiagnosticDetail>> CollectErrorsAsync(
        Solution solution,
        IReadOnlyList<ProjectId> scope,
        CancellationToken cancellationToken)
    {
        var baseDirectory = BaseDirectoryOf(solution);
        var errors = new List<DiagnosticDetail>();

        foreach (var projectId in scope)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var project = solution.GetProject(projectId);
            if (project is null || !project.SupportsCompilation)
            {
                continue;
            }

            errors.AddRange(await ErrorsForProjectAsync(project, baseDirectory, cancellationToken));
        }

        return errors;
    }

    /// <summary>
    /// One project's errors, served from the cache when this exact project state has already been
    /// compiled. The default <c>previewOnly</c> flow verifies the same baseline over and over while
    /// the candidate changes, so this is what turns two compilations per edit into one.
    /// </summary>
    private async Task<IReadOnlyList<DiagnosticDetail>> ErrorsForProjectAsync(
        Project project,
        string? baseDirectory,
        CancellationToken cancellationToken)
    {
        var filePath = project.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            // Nothing stable to key on (an in-memory project). Compile it every time rather than
            // risk serving one anonymous project's diagnostics for another's.
            return await ComputeErrorsAsync(project, baseDirectory, cancellationToken);
        }

        // What this cache does and does not buy, measured rather than assumed (2026-08-20, Roslyn
        // 5.6.0): across a *reload* of the same project, the file path is stable but the ProjectId,
        // the dependent version AND the dependent semantic version all change. So an entry cached
        // before a reload can never be hit after one — with either key. Path-keying is therefore not
        // "the key that survives reloads"; it is simply the stable half of a key whose version half
        // is what actually decides, and it keeps two different projects from colliding.
        //
        // The hit this cache is really for is a *repeat verification against the same warm
        // workspace* — the default previewOnly flow, where the baseline is re-verified on every edit
        // while only the candidate changes. Once a write invalidates the workspace the entry misses,
        // and that is correct: the baseline genuinely changed.
        //
        // Both versions, not just the semantic one: the dependent *semantic* version tracks
        // consumable declarations, so a body-only edit leaves it untouched — and a body-only edit is
        // precisely how a write introduces a compiler error. Keying on it alone would serve a stale
        // baseline and blame the next edit for an error it did not cause.
        var key = new CacheKey(
            filePath,
            await project.GetDependentSemanticVersionAsync(cancellationToken),
            await project.GetDependentVersionAsync(cancellationToken));

        Task<List<DiagnosticDetail>> pending;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_cache.TryGetValue(key, out var entry))
            {
                entry.LastAccess = ++_accessCounter;
                pending = entry.Errors;
                _logger.LogDebug("Verification cache hit for {Project}", project.Name);
            }
            else
            {
                // Started and stored under the gate, so two concurrent misses on the same project
                // await one compilation instead of racing into two. The shared work deliberately
                // runs uncancelled: one caller walking away must not cancel it for the other.
                pending = ComputeErrorsAsync(project, baseDirectory, CancellationToken.None);
                _cache[key] = new CacheEntry { Errors = pending, LastAccess = ++_accessCounter };
                EvictWhileOverBound();
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await pending.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!pending.IsFaulted)
        {
            // Our own cancellation; the shared computation is still good for whoever else wants it.
            throw;
        }
        catch
        {
            // Never keep a faulted result: the key is immutable, so a cached failure would be permanent.
            await ForgetAsync(key, pending);
            throw;
        }
    }

    /// <summary>Compiles one project and projects its errors into detached, workspace-free values.</summary>
    private async Task<List<DiagnosticDetail>> ComputeErrorsAsync(
        Project project,
        string? baseDirectory,
        CancellationToken cancellationToken)
    {
        var errors = new List<DiagnosticDetail>();

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
        {
            _logger.LogWarning("Project {Project} produced no compilation; skipping it in verification", project.Name);
            return errors;
        }

        var computed = await _diagnostics.GetDiagnosticsAsync(project, compilation, cancellationToken);
        foreach (var diagnostic in computed.Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors.Add(ToDetail(diagnostic, project.Name, baseDirectory));
            }
        }

        return errors;
    }

    private async Task ForgetAsync(CacheKey key, Task<List<DiagnosticDetail>> pending)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_cache.TryGetValue(key, out var entry) && ReferenceEquals(entry.Errors, pending))
            {
                _cache.Remove(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Evicts least-recently-used entries. Callers must hold <see cref="_gate"/>.</summary>
    private void EvictWhileOverBound()
    {
        while (_cache.Count > MaxCacheEntries)
        {
            var oldest = _cache.MinBy(pair => pair.Value.LastAccess).Key;
            _cache.Remove(oldest);
        }
    }

    /// <summary>
    /// Identity of one compiled project state. The path is stable across reloads; the two versions
    /// together change on any source edit, declaration-level or not.
    /// </summary>
    private readonly record struct CacheKey(string FilePath, VersionStamp SemanticVersion, VersionStamp Version);

    private sealed class CacheEntry
    {
        /// <summary>
        /// Detached values only — never <see cref="Diagnostic"/>, <see cref="Compilation"/> or
        /// <see cref="Location"/>, which would root a <see cref="SyntaxTree"/> into a workspace whose
        /// memory is never returned to the OS (see <c>docs/ARCHITECTURE.md</c> § Memory Management).
        /// </summary>
        public required Task<List<DiagnosticDetail>> Errors { get; init; }

        public long LastAccess { get; set; }
    }

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static readonly CacheKeyComparer Instance = new();

        public bool Equals(CacheKey x, CacheKey y) =>
            x.SemanticVersion == y.SemanticVersion
            && x.Version == y.Version
            && PathComparer.Equals(x.FilePath, y.FilePath);

        public int GetHashCode(CacheKey obj) =>
            HashCode.Combine(PathComparer.GetHashCode(obj.FilePath), obj.SemanticVersion, obj.Version);
    }

    /// <summary>
    /// Marks the service disposed and drops the cached values. The <see cref="SemaphoreSlim"/> is
    /// deliberately <b>not</b> disposed.
    /// </summary>
    /// <remarks>
    /// Disposing it would race a tool call already inside <c>WaitAsync</c> at host shutdown, which
    /// surfaces as an <see cref="ObjectDisposedException"/> thrown from the wait itself — classified
    /// as an InternalError rather than the clean cancellation it actually is. A
    /// <see cref="SemaphoreSlim"/> whose <c>AvailableWaitHandle</c> is never touched holds no
    /// unmanaged resource, so leaving it to the GC costs nothing and removes the race outright.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Forward-slashed paths relative to the solution's own directory, falling back to the first
    /// project's when the solution has no path.
    /// <para>
    /// ⚠️ This is <em>not</em> yet the rule the navigation tools, <c>ApplyFixes</c> and the edit
    /// tools use: they anchor on <see cref="LoadedProject.BaseDirectory"/> — the directory of the
    /// <c>resolvedPath</c> reported in the same response (#181) — while this method only sees a
    /// <see cref="Solution"/> and has to re-derive the anchor. The two agree everywhere except the
    /// case #181 is about: a <c>.csproj</c> not listed in its nearest ancestor <c>.sln</c>, where
    /// Roslyn grafts the project onto the already-open solution and <c>Solution.FilePath</c> keeps
    /// naming a <c>.sln</c> that never contributed it. So a <c>verification.errors[].file</c> (and
    /// <c>check_compilation</c>'s <c>errors[]</c>) can still disagree with the <c>resolvedPath</c>
    /// beside it there. Closing that needs the anchor threaded through
    /// <see cref="IVerificationService.VerifyAsync"/> from each caller's loader handle, which is a
    /// public-signature change and is tracked separately rather than folded into #181's three sites.
    /// </para>
    /// </summary>
    private static string? BaseDirectoryOf(Solution solution) =>
        Path.GetDirectoryName(solution.FilePath ?? solution.Projects.FirstOrDefault()?.FilePath);

    private static DiagnosticDetail ToDetail(Diagnostic diagnostic, string projectName, string? baseDirectory)
    {
        var span = diagnostic.Location.GetLineSpan();
        var path = span.Path;
        return new DiagnosticDetail
        {
            Project = projectName,
            File = string.IsNullOrEmpty(path) ? string.Empty : SymbolResolver.Relativize(path, baseDirectory) ?? path,
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            Id = diagnostic.Id,
            Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            Message = diagnostic.GetMessage()
        };
    }

    /// <summary>
    /// Caps a diagnostic list at <paramref name="max"/>, reporting the drop count rather than
    /// silently truncating. Returns <see langword="null"/> for an empty list so the field is omitted
    /// from the wire entirely.
    /// </summary>
    private static List<DiagnosticDetail>? Truncate(List<DiagnosticDetail> diagnostics, int max, out int omitted)
    {
        omitted = 0;
        if (diagnostics.Count == 0)
        {
            return null;
        }

        var ordered = diagnostics
            .OrderBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Column)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        // Clamped, never unbounded: `max: 0` is a natural way to ask for "just the verdict", and
        // reading it as "every diagnostic" would hand back exactly the thousands-of-binding-errors
        // blow-up this cap exists to prevent. Matches CodeNavigationService, which clamps the same way.
        var effectiveMax = Math.Max(1, max);
        if (ordered.Count <= effectiveMax)
        {
            return ordered;
        }

        omitted = ordered.Count - effectiveMax;
        return ordered.GetRange(0, effectiveMax);
    }
}
