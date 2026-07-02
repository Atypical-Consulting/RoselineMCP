using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using ReferenceLocation = RoselineMCP.Models.ReferenceLocation;

namespace RoselineMCP.Services;

/// <summary>
/// Roslyn-backed implementation of <see cref="ICodeNavigationService"/>. Loads the project (and its
/// solution, when present) once per call and answers structural/semantic questions with compact
/// results, so an AI agent can navigate code without reading whole files.
/// </summary>
public class CodeNavigationService : ICodeNavigationService
{
    /// <summary>Hard ceiling on call-graph traversal depth to keep responses bounded.</summary>
    private const int MaxCallGraphDepth = 3;

    private readonly ILogger<CodeNavigationService> _logger;
    private readonly IProjectLoader _projectLoader;

    /// <summary>Initializes a new instance of the <see cref="CodeNavigationService"/>.</summary>
    public CodeNavigationService(ILogger<CodeNavigationService> logger, IProjectLoader projectLoader)
    {
        _logger = logger;
        _projectLoader = projectLoader;
    }

    /// <inheritdoc/>
    public async Task<SymbolSearchResponse> SearchSymbolsAsync(
        string project,
        string? query,
        string? file,
        string[]? kinds,
        int max,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("Provide a 'query' pattern to search symbols, or a 'file' to outline.");
        }

        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var kindFilter = NormalizeKinds(kinds);

        var symbols = file != null
            ? await OutlineFileAsync(loaded.Project, file, query, cancellationToken)
            : await SearchProjectAsync(loaded.Project, query!, cancellationToken);

        var filtered = symbols.Where(s => MatchesKinds(s, kindFilter)).ToList();

        var ordered = filtered
            .OrderBy(s => SymbolResolver.LocationOf(s).Line ?? int.MaxValue)
            .ThenBy(s => s.ToDisplayString(SymbolResolver.FullNameFormat), StringComparer.Ordinal)
            .ToList();

        // A single-file outline shares one file and puts accessibility inside each signature, so it
        // returns a lean per-symbol projection; a project-wide search spans files and needs the full
        // summary (file + fully-qualified name).
        Func<ISymbol, SymbolSummary> toSummary = file != null ? LeanOutlineSummary : SymbolResolver.ToSummary;
        var capped = ordered.Take(Math.Max(1, max)).Select(toSummary).ToList();

        return new SymbolSearchResponse
        {
            Project = loaded.Project.Name,
            Query = query,
            File = file,
            TotalFound = ordered.Count,
            Truncated = ordered.Count > capped.Count,
            Symbols = capped
        };
    }

    private static async Task<List<ISymbol>> SearchProjectAsync(Project project, string query, CancellationToken cancellationToken)
    {
        var matcher = BuildNameMatcher(query);
        var found = await SymbolFinder.FindSourceDeclarationsAsync(project, matcher, SymbolFilter.All, cancellationToken);
        return found
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<ISymbol>()
            .ToList();
    }

    private async Task<List<ISymbol>> OutlineFileAsync(Project project, string file, string? query, CancellationToken cancellationToken)
    {
        var document = FindDocument(project, file)
            ?? throw new KeyNotFoundException($"File not found in project '{project.Name}': {file}");

        var model = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (model == null || root == null)
        {
            return new List<ISymbol>();
        }

        var matcher = string.IsNullOrWhiteSpace(query) ? null : BuildNameMatcher(query);
        var symbols = new List<ISymbol>();

        foreach (var node in root.DescendantNodes())
        {
            foreach (var symbol in DeclaredSymbols(model, node, cancellationToken))
            {
                if (matcher == null || matcher(symbol.Name))
                {
                    symbols.Add(symbol);
                }
            }
        }

        return symbols.Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();
    }

    private static IEnumerable<ISymbol> DeclaredSymbols(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    var symbol = model.GetDeclaredSymbol(variable, cancellationToken);
                    if (symbol != null) yield return symbol;
                }
                break;
            case BaseTypeDeclarationSyntax:
            case DelegateDeclarationSyntax:
            case MethodDeclarationSyntax:
            case ConstructorDeclarationSyntax:
            case PropertyDeclarationSyntax:
            case IndexerDeclarationSyntax:
            case EventDeclarationSyntax:
            case OperatorDeclarationSyntax:
            case EnumMemberDeclarationSyntax:
                var declared = model.GetDeclaredSymbol(node, cancellationToken);
                if (declared != null) yield return declared;
                break;
        }
    }

    /// <inheritdoc/>
    public async Task<SymbolInfoResponse> GetSymbolInfoAsync(
        string project,
        string symbol,
        bool includeSource,
        CancellationToken cancellationToken = default)
    {
        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Project, symbol, cancellationToken);

        var (file, line) = SymbolResolver.LocationOf(resolved);

        var response = new SymbolInfoResponse
        {
            Name = resolved.Name,
            FullName = resolved.ToDisplayString(SymbolResolver.FullNameFormat),
            Kind = SymbolResolver.KindOf(resolved),
            Accessibility = resolved.DeclaredAccessibility.ToString().ToLowerInvariant(),
            Modifiers = SymbolResolver.ModifiersOf(resolved),
            Signature = resolved.ToDisplayString(SymbolResolver.SignatureFormat),
            Documentation = ExtractSummary(resolved.GetDocumentationCommentXml(cancellationToken: cancellationToken)),
            DefinitionFile = file,
            DefinitionLine = line
        };

        if (resolved is INamedTypeSymbol namedType)
        {
            for (var baseType = namedType.BaseType; baseType != null && baseType.SpecialType != SpecialType.System_Object; baseType = baseType.BaseType)
            {
                response.BaseTypes.Add(baseType.ToDisplayString(SymbolResolver.FullNameFormat));
            }

            response.Interfaces = namedType.Interfaces
                .Select(i => i.ToDisplayString(SymbolResolver.FullNameFormat))
                .ToList();
        }

        if (includeSource)
        {
            var syntaxRef = resolved.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef != null)
            {
                response.Source = (await syntaxRef.GetSyntaxAsync(cancellationToken)).ToString();
            }
        }

        return response;
    }

    /// <inheritdoc/>
    public async Task<ReferencesResponse> FindReferencesAsync(
        string project,
        string symbol,
        bool includeDefinition,
        int max,
        CancellationToken cancellationToken = default)
    {
        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Project, symbol, cancellationToken);

        var referenced = await SymbolFinder.FindReferencesAsync(resolved, loaded.Solution, cancellationToken);

        var locations = new List<Location>();
        foreach (var reference in referenced)
        {
            if (includeDefinition)
            {
                locations.AddRange(reference.Definition.Locations.Where(l => l.IsInSource));
            }

            locations.AddRange(reference.Locations
                .Where(l => !l.IsImplicit && l.Location.IsInSource)
                .Select(l => l.Location));
        }

        var ordered = locations
            .Select(ToReferenceLocation)
            .OrderBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToList();

        var capped = ordered.Take(Math.Max(1, max)).ToList();

        return new ReferencesResponse
        {
            Symbol = resolved.Name,
            FullName = resolved.ToDisplayString(SymbolResolver.FullNameFormat),
            TotalReferences = ordered.Count,
            Truncated = ordered.Count > capped.Count,
            References = capped
        };
    }

    private static ReferenceLocation ToReferenceLocation(Location location)
    {
        var span = location.GetLineSpan();
        var snippet = string.Empty;
        var sourceText = location.SourceTree?.GetText();
        if (sourceText != null)
        {
            var lineIndex = span.StartLinePosition.Line;
            if (lineIndex >= 0 && lineIndex < sourceText.Lines.Count)
            {
                snippet = sourceText.Lines[lineIndex].ToString().Trim();
            }
        }

        return new ReferenceLocation
        {
            File = span.Path,
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            Snippet = snippet
        };
    }

    /// <inheritdoc/>
    public async Task<ImplementationsResponse> FindImplementationsAsync(
        string project,
        string symbol,
        int max,
        CancellationToken cancellationToken = default)
    {
        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Project, symbol, cancellationToken);
        var solution = loaded.Solution;

        var results = new List<ISymbol>();

        if (resolved is INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                results.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, projects: null, cancellationToken));
                results.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, projects: null, cancellationToken));
            }
            else
            {
                results.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, projects: null, cancellationToken));
            }
        }
        else if (resolved is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            results.AddRange(await SymbolFinder.FindImplementationsAsync(resolved, solution, projects: null, cancellationToken));
            results.AddRange(await SymbolFinder.FindOverridesAsync(resolved, solution, projects: null, cancellationToken));
        }
        else
        {
            throw new ArgumentException(
                $"find_implementations expects an interface, class, or member symbol, but '{symbol}' is a {SymbolResolver.KindOf(resolved)}.");
        }

        var ordered = results
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<ISymbol>()
            .OrderBy(s => s.ToDisplayString(SymbolResolver.FullNameFormat), StringComparer.Ordinal)
            .ToList();

        var capped = ordered.Take(Math.Max(1, max)).Select(SymbolResolver.ToSummary).ToList();

        return new ImplementationsResponse
        {
            Symbol = resolved.Name,
            FullName = resolved.ToDisplayString(SymbolResolver.FullNameFormat),
            Kind = SymbolResolver.KindOf(resolved),
            TotalFound = ordered.Count,
            Truncated = ordered.Count > capped.Count,
            Implementations = capped
        };
    }

    /// <inheritdoc/>
    public async Task<CallGraphResponse> GetCallGraphAsync(
        string project,
        string method,
        string direction,
        int depth,
        int max,
        CancellationToken cancellationToken = default)
    {
        var normalizedDirection = NormalizeDirection(direction, "callers", "callees", "both", defaultValue: "callers");
        var boundedDepth = Math.Clamp(depth <= 0 ? 1 : depth, 1, MaxCallGraphDepth);

        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Project, method, cancellationToken);

        if (resolved is not IMethodSymbol methodSymbol)
        {
            throw new ArgumentException(
                $"get_call_graph expects a method, but '{method}' is a {SymbolResolver.KindOf(resolved)}.");
        }

        var response = new CallGraphResponse
        {
            Method = methodSymbol.Name,
            // Parameter-qualified to match the node keys used for cycle detection below.
            FullName = methodSymbol.ToDisplayString(SymbolResolver.FullNameWithParamsFormat),
            Direction = normalizedDirection,
            Depth = boundedDepth
        };

        var budget = new Counter { Remaining = Math.Max(1, max) };

        if (normalizedDirection is "callers" or "both")
        {
            response.Callers = await BuildCallersAsync(methodSymbol, loaded.Solution, boundedDepth,
                new HashSet<string> { response.FullName }, budget, cancellationToken);
        }

        if (normalizedDirection is "callees" or "both")
        {
            budget.Remaining = Math.Max(1, max);
            response.Callees = await BuildCalleesAsync(methodSymbol, loaded.Solution, boundedDepth,
                new HashSet<string> { response.FullName }, budget, cancellationToken);
        }

        return response;
    }

    private async Task<List<CallGraphNode>> BuildCallersAsync(
        IMethodSymbol method, Solution solution, int depth, HashSet<string> path, Counter budget, CancellationToken cancellationToken)
    {
        var nodes = new List<CallGraphNode>();
        var callers = await SymbolFinder.FindCallersAsync(method, solution, cancellationToken);

        foreach (var caller in callers)
        {
            if (budget.Remaining <= 0) break;
            budget.Remaining--;

            var callingSymbol = caller.CallingSymbol;
            var node = ToNode(callingSymbol);
            var key = node.FullName;

            if (path.Contains(key))
            {
                node.Truncated = true;
            }
            else if (depth - 1 > 0 && callingSymbol is IMethodSymbol callingMethod)
            {
                path.Add(key);
                node.Children = await BuildCallersAsync(callingMethod, solution, depth - 1, path, budget, cancellationToken);
                path.Remove(key);
            }
            else
            {
                node.Truncated = true;
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private async Task<List<CallGraphNode>> BuildCalleesAsync(
        IMethodSymbol method, Solution solution, int depth, HashSet<string> path, Counter budget, CancellationToken cancellationToken)
    {
        var nodes = new List<CallGraphNode>();
        var callees = await GetCalleesAsync(method, solution, cancellationToken);

        foreach (var callee in callees)
        {
            if (budget.Remaining <= 0) break;
            budget.Remaining--;

            var node = ToNode(callee);
            var key = node.FullName;

            if (path.Contains(key))
            {
                node.Truncated = true;
            }
            else if (depth - 1 > 0)
            {
                path.Add(key);
                node.Children = await BuildCalleesAsync(callee, solution, depth - 1, path, budget, cancellationToken);
                path.Remove(key);
            }
            else
            {
                node.Truncated = true;
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private static async Task<List<IMethodSymbol>> GetCalleesAsync(IMethodSymbol method, Solution solution, CancellationToken cancellationToken)
    {
        var result = new List<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var node = await syntaxRef.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(node.SyntaxTree);
            if (document == null) continue;

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null) continue;

            foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol called
                    && seen.Add(called.OriginalDefinition))
                {
                    result.Add(called.OriginalDefinition);
                }
            }

            foreach (var creation in node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol ctor
                    && seen.Add(ctor.OriginalDefinition))
                {
                    result.Add(ctor.OriginalDefinition);
                }
            }
        }

        return result;
    }

    private static CallGraphNode ToNode(ISymbol symbol)
    {
        var (file, line) = SymbolResolver.LocationOf(symbol);
        return new CallGraphNode
        {
            // Parameter-qualified so overloads get distinct node keys — otherwise cycle detection
            // would treat a call to a different overload of an ancestor as a false cycle.
            FullName = symbol.ToDisplayString(SymbolResolver.FullNameWithParamsFormat),
            Signature = symbol.ToDisplayString(SymbolResolver.SignatureFormat),
            File = file,
            Line = line
        };
    }

    /// <inheritdoc/>
    public async Task<TypeHierarchyResponse> GetTypeHierarchyAsync(
        string project,
        string type,
        string direction,
        int max,
        CancellationToken cancellationToken = default)
    {
        var normalizedDirection = NormalizeDirection(direction, "base", "derived", "both", defaultValue: "both");

        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Project, type, cancellationToken);

        if (resolved is not INamedTypeSymbol namedType)
        {
            throw new ArgumentException(
                $"get_type_hierarchy expects a type, but '{type}' is a {SymbolResolver.KindOf(resolved)}.");
        }

        var response = new TypeHierarchyResponse
        {
            Type = namedType.Name,
            FullName = namedType.ToDisplayString(SymbolResolver.FullNameFormat),
            Direction = normalizedDirection
        };

        if (normalizedDirection is "base" or "both")
        {
            var baseTypes = new List<SymbolSummary>();
            for (var baseType = namedType.BaseType; baseType != null && baseType.SpecialType != SpecialType.System_Object; baseType = baseType.BaseType)
            {
                baseTypes.Add(SymbolResolver.ToSummary(baseType));
            }

            response.BaseTypes = baseTypes;
            response.Interfaces = namedType.AllInterfaces
                .Select(SymbolResolver.ToSummary)
                .ToList();
        }

        if (normalizedDirection is "derived" or "both")
        {
            var derived = new List<ISymbol>();
            if (namedType.TypeKind == TypeKind.Interface)
            {
                derived.AddRange(await SymbolFinder.FindImplementationsAsync(namedType, loaded.Solution, transitive: true, projects: null, cancellationToken));
                derived.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(namedType, loaded.Solution, transitive: true, projects: null, cancellationToken));
            }
            else
            {
                derived.AddRange(await SymbolFinder.FindDerivedClassesAsync(namedType, loaded.Solution, transitive: true, projects: null, cancellationToken));
            }

            var orderedDerived = derived
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<ISymbol>()
                .OrderBy(s => s.ToDisplayString(SymbolResolver.FullNameFormat), StringComparer.Ordinal)
                .ToList();

            var cappedDerived = orderedDerived.Take(Math.Max(1, max)).Select(SymbolResolver.ToSummary).ToList();
            response.DerivedTypes = cappedDerived;
            response.DerivedTypesTruncated = orderedDerived.Count > cappedDerived.Count;
        }

        return response;
    }

    /// <summary>
    /// Lean per-symbol projection for a single-file outline: name, kind, signature (which already
    /// carries accessibility, modifiers, and return type), line, and containing type. The file path
    /// and fully-qualified name are intentionally omitted — the file is on the response and the FQN
    /// is <c>containingType + name</c> — so the outline does not repeat them on every symbol.
    /// </summary>
    private static SymbolSummary LeanOutlineSummary(ISymbol symbol)
    {
        var (_, line) = SymbolResolver.LocationOf(symbol);
        return new SymbolSummary
        {
            Name = symbol.Name,
            Kind = SymbolResolver.KindOf(symbol),
            Signature = symbol.ToDisplayString(SymbolResolver.SignatureFormat),
            Line = line,
            ContainingType = symbol.ContainingType?.ToDisplayString(SymbolResolver.FullNameFormat)
        };
    }

    private static Document? FindDocument(Project project, string file)
    {
        var normalized = file.Replace('\\', '/');
        return project.Documents.FirstOrDefault(d => IsPathSuffixMatch(d.FilePath, normalized))
               ?? project.Documents.FirstOrDefault(d =>
                   d.Name.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Matches <paramref name="normalizedQuery"/> against a document path only on directory-segment
    /// boundaries, so "Service.cs" matches ".../Service.cs" but not ".../UserService.cs".
    /// </summary>
    private static bool IsPathSuffixMatch(string? filePath, string normalizedQuery)
    {
        if (filePath == null)
        {
            return false;
        }

        var normalizedPath = filePath.Replace('\\', '/');
        return normalizedPath.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.EndsWith("/" + normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static Func<string, bool> BuildNameMatcher(string query)
    {
        if (query.Contains('*') || query.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return name => regex.IsMatch(name);
        }

        return name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> NormalizeKinds(string[]? kinds) =>
        kinds == null
            ? new HashSet<string>()
            : kinds.Where(k => !string.IsNullOrWhiteSpace(k))
                   .Select(k => k.Trim().ToLowerInvariant())
                   .ToHashSet();

    private static bool MatchesKinds(ISymbol symbol, HashSet<string> kinds)
    {
        if (kinds.Count == 0) return true;

        var kind = SymbolResolver.KindOf(symbol);
        if (kinds.Contains(kind)) return true;
        if (kinds.Contains("type") && symbol is INamedTypeSymbol) return true;
        if (kinds.Contains("member") && symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol) return true;

        return false;
    }

    private static string NormalizeDirection(string? direction, string a, string b, string both, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(direction)) return defaultValue;

        var normalized = direction.Trim().ToLowerInvariant();
        if (normalized == a || normalized == b || normalized == both) return normalized;

        throw new ArgumentException($"Invalid direction '{direction}'. Valid values: {a}, {b}, {both}.");
    }

    private static string? ExtractSummary(string? documentationXml)
    {
        if (string.IsNullOrWhiteSpace(documentationXml)) return null;

        try
        {
            var doc = XDocument.Parse(documentationXml);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary == null) return null;

            var text = string.Concat(summary.Nodes().Select(n => n is XElement element ? element.Value : n.ToString()));
            var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
            return collapsed.Length == 0 ? null : collapsed;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Mutable traversal budget shared across recursive call-graph expansion.</summary>
    private sealed class Counter
    {
        public int Remaining { get; set; }
    }
}
