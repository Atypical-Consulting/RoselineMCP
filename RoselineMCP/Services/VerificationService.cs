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
public class VerificationService : IVerificationService
{
    private readonly ILogger<VerificationService> _logger;
    private readonly IDiagnosticComputationService _diagnostics;

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

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                _logger.LogWarning("Project {Project} produced no compilation; skipping it in verification", project.Name);
                continue;
            }

            var diagnostics = await _diagnostics.GetDiagnosticsAsync(project, compilation, cancellationToken);
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    errors.Add(ToDetail(diagnostic, project.Name, baseDirectory));
                }
            }
        }

        return errors;
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
