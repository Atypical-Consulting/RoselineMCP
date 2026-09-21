using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoselineMCP.Services;

/// <summary>
/// Removes the <see cref="AnalyzerReference"/>s Roslyn's own project-state checksum cannot
/// serialize, so a solution can be handed to <c>SymbolFinder</c> and <c>Renamer</c> without
/// aborting the call.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's <c>SerializerService.CreateChecksum(AnalyzerReference)</c> knows exactly two reference
/// kinds — <see cref="AnalyzerFileReference"/> and its in-memory image counterpart — and throws
/// <see cref="InvalidOperationException"/> (<c>Unexpected value '…' of type '…'</c>) for anything
/// else. That checksum is reached from <c>ProjectState.GetChecksumAsync</c>, which
/// <c>DependentTypeFinder.ProjectIndex.GetIndexAsync</c> asks for — so <b>every</b> relationship
/// query (<c>find_references</c>, <c>find_implementations</c>, <c>get_call_graph</c>,
/// <c>get_type_hierarchy</c>) and <c>Renamer.RenameSymbolAsync</c> behind <c>rename_symbol</c> fail
/// outright on a solution carrying one (issue #242).
/// </para>
/// <para>
/// The kind that occurs in practice is <c>UnresolvedAnalyzerReference</c>: MSBuild's project loader
/// emits it for an <c>&lt;Analyzer Include&gt;</c> whose assembly is not on disk — routine in a git
/// worktree whose <c>obj/</c> was populated elsewhere, or after a partial restore. Removing it is
/// <b>semantics-free</b>: the sentinel returns an empty array from <c>GetAnalyzers</c> and from
/// <c>GetGenerators</c>, so no analyzer stops running and no generated type disappears. What is lost
/// is only the knowledge that the reference was there — which is why the removed paths travel on
/// <c>LoadedProject.UnresolvedAnalyzerReferences</c> and are named in the <c>analyzerLoad</c> block
/// rather than dropped silently.
/// </para>
/// <para>
/// The predicate is an allow-list, which is the risky direction: too narrow and it deletes working
/// analyzers and source generators from every loaded solution. <c>AnalyzerReferenceFilterTests</c>
/// pins it against this repository's own project loaded through the production loader, where
/// MSBuildWorkspace 5.9.0 emits plain <see cref="AnalyzerFileReference"/>s only.
/// </para>
/// </remarks>
internal static class AnalyzerReferenceFilter
{
    /// <summary>
    /// Whether Roslyn's project-state checksum can serialize <paramref name="reference"/> — i.e.
    /// whether a solution carrying it survives a relationship query.
    /// </summary>
    internal static bool IsSerializable(AnalyzerReference reference) =>
        reference is AnalyzerFileReference or AnalyzerImageReference;

    /// <summary>
    /// Returns <paramref name="solution"/> with every non-serializable analyzer reference removed
    /// from <b>every</b> project, plus the paths of those removed from <paramref name="anchor"/>
    /// alone (deduplicated, first-seen order). A solution with nothing to remove is handed back
    /// <b>unchanged</b> — the same instance, no fork — so the universal clean case costs one
    /// predicate call per reference and nothing else.
    /// </summary>
    /// <param name="solution">The solution to strip.</param>
    /// <param name="anchor">The project the caller asked about — the one the report is about.</param>
    /// <remarks>
    /// The two scopes differ on purpose. Removal must span the solution, because the checksum that
    /// throws is asked for across it: a sibling project's unresolved reference breaks a relationship
    /// query on the anchor just as its own would. Reporting must not, because <c>analyzerLoad</c>'s
    /// counters are defined over <b>the target project's</b> references
    /// (<c>AnalyzerLoadReport.ReferencesConsulted</c>) — attributing a sibling's stale reference to
    /// the caller's project would inflate that count and make a clean project read as degraded.
    /// Nothing is lost by the silence: the removal is semantics-free either way.
    /// </remarks>
    internal static (Solution Solution, IReadOnlyList<string> Removed) Strip(Solution solution, ProjectId anchor)
    {
        List<string>? removed = null;
        HashSet<string>? seen = null;

        // ProjectIds is an immutable snapshot, so iterating it while re-forking `solution` is safe;
        // the project itself is re-fetched from the current fork each time round.
        foreach (var projectId in solution.ProjectIds)
        {
            var references = solution.GetProject(projectId)!.AnalyzerReferences;
            if (references.All(IsSerializable))
            {
                continue;
            }

            var kept = new List<AnalyzerReference>(references.Count);
            foreach (var reference in references)
            {
                if (IsSerializable(reference))
                {
                    kept.Add(reference);
                    continue;
                }

                if (projectId != anchor)
                {
                    // Removed all the same — just not the caller's to hear about. See the remarks.
                    continue;
                }

                removed ??= [];
                seen ??= new HashSet<string>(StringComparer.Ordinal);
                var path = reference.FullPath ?? reference.Display ?? reference.GetType().Name;
                if (seen.Add(path))
                {
                    removed.Add(path);
                }
            }

            solution = solution.WithProjectAnalyzerReferences(projectId, kept);
        }

        return (solution, removed ?? (IReadOnlyList<string>)[]);
    }
}
