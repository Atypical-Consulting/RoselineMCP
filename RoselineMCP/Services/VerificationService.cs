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
        var verdict = new VerificationVerdict
        {
            Scope = scope.Select(id => candidate.GetProject(id)?.Name ?? id.ToString())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList(),
            ScopeComplete = true
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

        // The path, not the ProjectId: CachingProjectLoader mints fresh ProjectId GUIDs every time it
        // reloads after a write, so an id-keyed cache would miss on exactly the calls it exists for.
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

        var diagnostics = await _diagnostics.GetDiagnosticsAsync(project, compilation, cancellationToken);
        foreach (var diagnostic in diagnostics)
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

    /// <summary>Releases the cache gate. The cached values themselves hold no unmanaged resources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Solution-root-relative, forward-slashed paths — the same rule the navigation tools,
    /// <c>ApplyFixes</c> and <c>EditMember</c> use, so a file has one canonical path across every
    /// tool's output.
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

        if (max <= 0 || ordered.Count <= max)
        {
            return ordered;
        }

        omitted = ordered.Count - max;
        return ordered.GetRange(0, max);
    }
}
