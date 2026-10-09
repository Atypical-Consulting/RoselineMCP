using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Implements <see cref="IFixExampleService"/>. The analyzer is the predicate: a site "satisfies the
/// rule" iff the diagnostic does not fire there, so retrieval is the set difference between the
/// shape-matched sites and the diagnostic set the shared computation service already produces.
/// Read-only: it never builds a candidate solution and never writes.
/// </summary>
public class FixExampleService : IFixExampleService
{
    private readonly ILogger<FixExampleService> _logger;
    private readonly IProjectLoader _projectLoader;
    private readonly IDiagnosticComputationService _computation;
    private readonly IDiagnosticFilterService _filterService;

    /// <summary>Creates the service.</summary>
    public FixExampleService(
        ILogger<FixExampleService> logger,
        IProjectLoader projectLoader,
        IDiagnosticComputationService computation,
        IDiagnosticFilterService filterService)
    {
        _logger = logger;
        _projectLoader = projectLoader;
        _computation = computation;
        _filterService = filterService;
    }

    /// <inheritdoc/>
    public async Task<FixExamplesResponse> SuggestAsync(
        string? project, string id, string? file, int? line,
        int maxExamples, int maxCandidates, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
            try
            {
                return await BuildAsync(loaded, id, file, line, maxExamples, maxCandidates, cancellationToken);
            }
            catch (Exception ex)
            {
                ResolvedPathStamp.Stamp(ex, loaded);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suggest fix examples");
            throw;
        }
    }

    private async Task<FixExamplesResponse> BuildAsync(
        LoadedProject loaded, string id, string? file, int? line,
        int maxExamples, int maxCandidates, CancellationToken ct)
    {
        id = id.Trim();
        var anchor = loaded.Project;
        var response = new FixExamplesResponse
        {
            ResolvedPath = loaded.ResolvedPath,
            Rule = new RuleInfo { Id = id },
            HasFixer = _filterService.IsFixableDiagnostic(id, anchor),
        };

        var anchorFires = await FiresAsync(anchor, id, ct);
        if (anchorFires.Count == 0)
        {
            response.Notes.Add($"{id} does not fire in project '{anchor.Name}', so there is no alert to find examples for.");
            return response;
        }

        response.Rule = RuleOf(anchorFires[0]);
        if (response.HasFixer)
        {
            response.Notes.Add($"{id} has a registered code fixer — apply_fixes may repair it directly.");
        }

        var occurrences = anchorFires
            .Where(d => d.Location.IsInSource)
            .OrderBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ToList();
        if (occurrences.Count == 0)
        {
            response.Notes.Add($"{id} fires only without a source location; no shape can be derived.");
            return response;
        }

        var chosen = PickOccurrence(occurrences, file, line, response.Notes);
        var alertTree = chosen.Location.SourceTree!;
        var alertNode = alertTree.GetRoot(ct).FindNode(chosen.Location.SourceSpan, getInnermostNodeForTie: true);
        var alertCompilation = await anchor.GetCompilationAsync(ct);
        var alertModel = alertCompilation?.GetSemanticModel(alertTree);
        var (alertKey, _) = ShapeKey.From(alertNode, alertModel);
        var alertKinds = ShapeKey.KindsOf(alertNode);
        var alertLine = chosen.Location.GetLineSpan().StartLinePosition.Line;

        response.Alert = new AlertSite
        {
            File = Rel(alertTree.FilePath, loaded),
            Line = alertLine + 1,
            Snippet = SnippetAt(alertTree, alertLine),
            ShapeKey = alertKey,
        };

        var found = new List<(FixExample Example, int Rank)>();
        var scanned = 0;
        var truncated = false;
        var alertTypeName = TypeNameOf(alertNode);
        var projects = SymbolResolver.OrderedCSharpProjects(loaded.Solution, anchor);

        foreach (var proj in projects)
        {
            ct.ThrowIfCancellationRequested();

            // Sibling projects are only consulted when the anchor could not fill the request: they
            // cost a diagnostics pass each, and rank below the anchor's own sites anyway.
            var isAnchor = proj.Id == anchor.Id;
            if (!isAnchor && found.Count >= maxExamples)
            {
                break;
            }

            var fires = isAnchor ? anchorFires : await FiresAsync(proj, id, ct);
            var compilation = isAnchor ? alertCompilation : await proj.GetCompilationAsync(ct);
            if (compilation is null)
            {
                continue;
            }

            var firesByPath = fires
                .Where(d => d.Location.IsInSource)
                .GroupBy(d => d.Location.SourceTree!.FilePath, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(d => d.Location.SourceSpan).ToList(), StringComparer.Ordinal);

            foreach (var tree in compilation.SyntaxTrees)
            {
                firesByPath.TryGetValue(tree.FilePath, out var treeFires);
                SemanticModel? model = null;

                foreach (var node in tree.GetRoot(ct).DescendantNodes().Where(n => n.RawKind == alertNode.RawKind))
                {
                    if (ShapeKey.KindsOf(node) != alertKinds)
                    {
                        continue;
                    }

                    if (++scanned > maxCandidates)
                    {
                        scanned = maxCandidates;
                        truncated = true;
                        break;
                    }

                    // The bridging predicate: the rule does not fire anywhere inside this node.
                    if (treeFires is not null && treeFires.Any(s => node.Span.IntersectsWith(s)))
                    {
                        continue;
                    }

                    model ??= compilation.GetSemanticModel(tree);
                    var (key, matchedOn) = ShapeKey.From(node, model);
                    if (key != alertKey)
                    {
                        continue;
                    }

                    var nodeLine = tree.GetLineSpan(node.Span).StartLinePosition.Line;
                    var rank = isAnchor ? 2 : 3;
                    if (isAnchor && tree.FilePath == alertTree.FilePath)
                    {
                        rank = alertTypeName is not null && TypeNameOf(node) == alertTypeName ? 0 : 1;
                    }

                    found.Add((new FixExample
                    {
                        File = Rel(tree.FilePath, loaded),
                        Line = nodeLine + 1,
                        Snippet = SnippetAt(tree, nodeLine),
                        MatchedOn = matchedOn,
                    }, rank));
                }

                if (truncated)
                {
                    break;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        var ordered = found
            .OrderBy(f => f.Rank)
            .ThenBy(f => f.Example.File, StringComparer.Ordinal)
            .ThenBy(f => f.Example.Line)
            .Select(f => f.Example)
            .ToList();

        response.CandidatesScanned = scanned;
        response.Truncated = truncated || ordered.Count > maxExamples;
        response.Examples = ordered.Take(maxExamples).ToList();

        if (truncated)
        {
            response.Notes.Add($"The scan stopped at maxCandidates={maxCandidates}; more clean sites may exist.");
        }

        if (response.Examples.Count == 0)
        {
            response.Notes.Add(
                $"No clean site of the same shape was found in the solution. The rule's own description and helpLinkUri are the best available guidance for {id}.");
        }

        return response;
    }

    private static Diagnostic PickOccurrence(List<Diagnostic> occurrences, string? file, int? line, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(file) || line is not null)
        {
            var normalizedFile = file?.Replace('\\', '/');
            var match = occurrences.FirstOrDefault(d =>
            {
                var path = d.Location.SourceTree!.FilePath.Replace('\\', '/');
                var fileOk = string.IsNullOrWhiteSpace(normalizedFile)
                    || path.Equals(normalizedFile, StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/" + normalizedFile.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
                var lineOk = line is null || d.Location.GetLineSpan().StartLinePosition.Line + 1 == line;
                return fileOk && lineOk;
            });

            if (match is not null)
            {
                return match;
            }

            notes.Add($"No occurrence at {file ?? "*"}:{line?.ToString() ?? "*"}; fell back to the first occurrence.");
        }

        return occurrences[0];
    }

    private async Task<List<Diagnostic>> FiresAsync(Project project, string id, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
        {
            return [];
        }

        var result = await _computation.GetDiagnosticsAsync(project, compilation, ct);
        return result.Diagnostics.Where(d => string.Equals(d.Id, id, StringComparison.Ordinal)).ToList();
    }

    private static RuleInfo RuleOf(Diagnostic diagnostic)
    {
        var d = diagnostic.Descriptor;
        var description = d.Description.ToString();
        return new RuleInfo
        {
            Id = d.Id,
            Title = d.Title.ToString(),
            Category = d.Category,
            DefaultSeverity = d.DefaultSeverity.ToString().ToLowerInvariant(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            HelpLinkUri = string.IsNullOrWhiteSpace(d.HelpLinkUri) ? null : d.HelpLinkUri,
        };
    }

    private static string? TypeNameOf(SyntaxNode node)
        => node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

    private static string SnippetAt(SyntaxTree tree, int zeroBasedLine)
    {
        var lines = tree.GetText().Lines;
        return zeroBasedLine >= 0 && zeroBasedLine < lines.Count ? lines[zeroBasedLine].ToString().Trim() : string.Empty;
    }

    private static string Rel(string path, LoadedProject loaded)
        => SymbolResolver.Relativize(path, loaded.BaseDirectory) ?? path;
}
