using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Shared helpers for resolving a caller-supplied symbol reference (simple name or fully-qualified
/// name) to a Roslyn <see cref="ISymbol"/>, and for projecting symbols into the compact
/// <see cref="SymbolSummary"/> DTO. Used by both the navigation and edit services so name resolution
/// and display formatting stay consistent.
/// </summary>
internal static class SymbolResolver
{
    /// <summary>Fully-qualified display: namespace + containing types + name, with type parameters.</summary>
    public static readonly SymbolDisplayFormat FullNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Like <see cref="FullNameFormat"/> but also includes a member's parameter types, so
    /// parameter-differing overloads render to distinct strings (e.g. <c>Calc.Add(int, int)</c> vs
    /// <c>Calc.Add(double, double)</c>). Used to disambiguate overloads when resolving a caller's
    /// symbol reference, when listing ambiguity candidates, and as call-graph node keys.
    /// </summary>
    public static readonly SymbolDisplayFormat FullNameWithParamsFormat = FullNameFormat
        .AddMemberOptions(SymbolDisplayMemberOptions.IncludeParameters)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut);

    /// <summary>Parameter <em>type</em> rendering as simple names (e.g. <c>CancellationToken</c>, <c>List&lt;string&gt;</c>).</summary>
    private static readonly SymbolDisplayFormat ShortParamTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Identity string for a call-graph node: the namespace-qualified container and method name, with
    /// parameter <em>types</em> rendered as simple names — e.g.
    /// <c>RoselineMCP.Services.CodeNavigationService.SearchSymbolsAsync(string, string, string[], int, CancellationToken)</c>.
    /// Keeps overload disambiguation and full container qualification (so it doubles as a stable cycle
    /// key) while dropping the fully-qualified parameter-type namespaces that otherwise repeat on
    /// every node.
    /// </summary>
    public static string CallNodeName(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            var container = symbol.ToDisplayString(FullNameFormat);
            var parameters = string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(ShortParamTypeFormat)));
            return $"{container}({parameters})";
        }

        return symbol.ToDisplayString(FullNameWithParamsFormat);
    }

    /// <summary>Human-readable signature: accessibility, modifiers, return type, name, parameters.</summary>
    public static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeConstantValue,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword | SymbolDisplayKindOptions.IncludeTypeKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Resolves <paramref name="query"/> to exactly one symbol, searching the declarations of every
    /// C# project in <paramref name="solution"/> (so symbols living in sibling projects the
    /// <paramref name="anchor"/> does not reference are still found). Throws
    /// <see cref="KeyNotFoundException"/> when nothing matches (→ NotFoundError) and
    /// <see cref="ArgumentException"/> when the reference is ambiguous (→ ValidationError), listing
    /// the candidate fully-qualified names so the caller can disambiguate.
    /// </summary>
    public static async Task<ISymbol> ResolveOrThrowAsync(
        Solution solution,
        Project anchor,
        string query,
        CancellationToken cancellationToken)
    {
        var matches = await ResolveAllAsync(solution, anchor, query, cancellationToken);

        if (matches.Count == 0)
        {
            throw new KeyNotFoundException(
                $"Symbol not found: '{query}'. Use search_symbols to discover exact names in this solution.");
        }

        if (matches.Count > 1)
        {
            // Use the parameter-qualified format so overloads are listed as distinct, copy-pasteable
            // candidates (a parameter-less FQN cannot tell two overloads apart).
            var candidates = string.Join(", ", matches
                .Select(m => m.ToDisplayString(FullNameWithParamsFormat))
                .Distinct()
                .Take(10));
            throw new ArgumentException(
                $"Ambiguous symbol '{query}' — {matches.Count} matches. Pass a fully-qualified name (including a parameter list for an overload) to disambiguate. Candidates: {candidates}");
        }

        return matches[0];
    }

    /// <summary>
    /// Returns all distinct symbols declared in <paramref name="solution"/> whose
    /// name/fully-qualified name matches <paramref name="query"/>, searching every C# project (not
    /// just the <paramref name="anchor"/>). Exact fully-qualified matches win over suffix matches,
    /// which win over simple-name matches, so the most specific interpretation is preferred. The
    /// same declaration seen through several project compilations counts once, preferring the
    /// <paramref name="anchor"/>'s symbol instance.
    /// </summary>
    public static async Task<List<ISymbol>> ResolveAllAsync(
        Solution solution,
        Project anchor,
        string query,
        CancellationToken cancellationToken)
    {
        var projects = OrderedCSharpProjects(solution, anchor);

        // Fast path: a fully-qualified type name (supports nested types via '+'). Every project's
        // compilation is consulted so a type living in a sibling project the anchor doesn't
        // reference is still found; the same declaration resolved through multiple compilations
        // (e.g. a referenced project's type) is deduplicated to a single candidate, while two
        // genuinely distinct declarations sharing one metadata name stay ambiguous.
        var metadataName = query.Replace('+', '.');
        var byMetadata = new List<ISymbol>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var type = compilation?.GetTypeByMetadataName(query)
                ?? compilation?.GetTypeByMetadataName(metadataName);
            if (type != null && seenTypes.Add(DeclarationKeyOf(type)))
            {
                byMetadata.Add(type);
            }
        }

        if (byMetadata.Count > 0)
        {
            return byMetadata;
        }

        // Strip any parameter list (e.g. "Calc.Add(int, int)" -> "Calc.Add") before extracting the
        // bare identifier that declaration search matches on.
        var nameForSearch = query;
        var parenIndex = nameForSearch.IndexOf('(');
        if (parenIndex >= 0)
        {
            nameForSearch = nameForSearch[..parenIndex];
        }

        var simpleName = nameForSearch.Contains('.') ? nameForSearch[(nameForSearch.LastIndexOf('.') + 1)..] : nameForSearch;
        if (string.IsNullOrWhiteSpace(simpleName))
        {
            return new List<ISymbol>();
        }

        // Declaration search per project, anchor first. SymbolEqualityComparer does NOT equate the
        // same declaration seen from different project compilations, so duplicates are collapsed by
        // declaring source location instead (keeping the anchor's instance).
        var candidates = new List<ISymbol>();
        var seenDeclarations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var declarations = await SymbolFinder.FindDeclarationsAsync(
                project, simpleName, ignoreCase: false, SymbolFilter.All, cancellationToken);

            foreach (var declaration in declarations)
            {
                if (declaration.Locations.Any(l => l.IsInSource) && seenDeclarations.Add(DeclarationKeyOf(declaration)))
                {
                    candidates.Add(declaration);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return candidates;
        }

        // Match against the parameter-qualified name when the caller included a parameter list
        // (e.g. "Calc.Add(int, int)" to select one overload), otherwise against the parameter-less
        // fully-qualified name. Whitespace is ignored so "(int,int)" and "(int, int)" both match.
        // Prefer an exact match, then a suffix match (e.g. "UserService.GetUser" matching
        // "Acme.Users.UserService.GetUser"), then fall back to every simple-name match.
        var hasParameterList = query.Contains('(');
        var format = hasParameterList ? FullNameWithParamsFormat : FullNameFormat;
        var normalizedQuery = StripWhitespace(query);

        var exact = candidates.Where(s => StripWhitespace(s.ToDisplayString(format)) == normalizedQuery).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        if (query.Contains('.'))
        {
            var suffix = candidates
                .Where(s => StripWhitespace(s.ToDisplayString(format)).EndsWith("." + normalizedQuery, StringComparison.Ordinal))
                .ToList();
            if (suffix.Count > 0)
            {
                return suffix;
            }
        }

        return candidates;
    }

    private static string StripWhitespace(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Every C# project in <paramref name="solution"/>, with the <paramref name="anchor"/> first so
    /// symbol search/dedup prefer its symbol instances, then the rest ordered by name for
    /// deterministic results.
    /// </summary>
    public static List<Project> OrderedCSharpProjects(Solution solution, Project anchor)
    {
        var projects = new List<Project> { anchor };
        projects.AddRange(solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.Id != anchor.Id)
            .OrderBy(p => p.Name, StringComparer.Ordinal));
        return projects;
    }

    /// <summary>
    /// Identity key for deduplicating the same declaration seen through different project
    /// compilations (where <see cref="SymbolEqualityComparer"/> treats the instances as unequal):
    /// the sorted set of in-source declaration locations (file path + span). Metadata-only symbols
    /// fall back to assembly identity + parameter-qualified display name.
    /// </summary>
    public static string DeclarationKeyOf(ISymbol symbol)
    {
        var sourceLocations = symbol.Locations
            .Where(l => l.IsInSource)
            .Select(l => $"{l.SourceTree?.FilePath}:{l.SourceSpan.Start}-{l.SourceSpan.End}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (sourceLocations.Count > 0)
        {
            return "src|" + string.Join("|", sourceLocations);
        }

        return $"meta|{symbol.ContainingAssembly?.Identity.GetDisplayName()}|{symbol.ToDisplayString(FullNameWithParamsFormat)}";
    }

    /// <summary>Projects a symbol into the compact <see cref="SymbolSummary"/> DTO.</summary>
    public static SymbolSummary ToSummary(ISymbol symbol)
    {
        var (file, line) = LocationOf(symbol);
        return new SymbolSummary
        {
            Name = symbol.Name,
            FullName = symbol.ToDisplayString(FullNameFormat),
            Kind = KindOf(symbol),
            Signature = symbol.ToDisplayString(SignatureFormat),
            File = file,
            Line = line
            // Accessibility is omitted: SignatureFormat already renders the accessibility keyword,
            // so a separate field would just repeat it.
            // ContainingType is intentionally omitted here: FullName already begins with the
            // containing type, so repeating it on every result is pure redundancy. Only the file
            // outline (which omits FullName) sets ContainingType — see LeanOutlineSummary.
        };
    }

    /// <summary>Friendly lowercase kind, e.g. <c>class</c>, <c>interface</c>, <c>method</c>, <c>property</c>.</summary>
    public static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        INamespaceSymbol => "namespace",
        IParameterSymbol => "parameter",
        ILocalSymbol => "local",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };

    /// <summary>Source file path (absolute) and 1-based line of a symbol's first in-source declaration.</summary>
    public static (string? File, int? Line) LocationOf(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location == null)
        {
            return (null, null);
        }

        var span = location.GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
    }

    /// <summary>
    /// Rewrites an absolute source path as <paramref name="baseDir"/>-relative with forward slashes,
    /// so navigation results don't repeat the workspace prefix on every symbol/reference/node (the
    /// single biggest source of redundant tokens). Paths outside <paramref name="baseDir"/> — or when
    /// it is unknown — are returned unchanged (slash-normalized), since a <c>../../</c> path saves
    /// nothing and reads worse.
    /// </summary>
    public static string? Relativize(string? absolutePath, string? baseDir)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return absolutePath;
        }

        var normalized = absolutePath.Replace('\\', '/');
        if (string.IsNullOrEmpty(baseDir))
        {
            return normalized;
        }

        var relative = Path.GetRelativePath(baseDir, absolutePath).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? normalized
            : relative;
    }

    /// <summary>Declaration modifiers present on a symbol (static/abstract/sealed/virtual/override/async/readonly/const).</summary>
    public static List<string> ModifiersOf(ISymbol symbol)
    {
        var modifiers = new List<string>();

        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsSealed) modifiers.Add("sealed");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");

        switch (symbol)
        {
            case IMethodSymbol { IsAsync: true }:
                modifiers.Add("async");
                break;
            case IFieldSymbol field:
                if (field.IsConst) modifiers.Add("const");
                if (field.IsReadOnly) modifiers.Add("readonly");
                break;
        }

        return modifiers;
    }
}
