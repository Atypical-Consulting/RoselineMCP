using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.TokenBenchmark;

// ── Locate the repo + the project to benchmark (dogfood RoselineMCP's own source) ──
const string TargetProject = "RoselineMCP";
var repoRoot = FindRepoRoot();
var mainCsproj = Path.Combine(repoRoot, "RoselineMCP", "RoselineMCP.csproj");
if (!File.Exists(mainCsproj))
{
    Console.Error.WriteLine($"Could not find {mainCsproj}");
    return 1;
}

Console.WriteLine($"RoselineMCP token-savings benchmark");
Console.WriteLine($"Repo: {repoRoot}");
Console.WriteLine($"Loading solution via MSBuild (once)…");

// ── Load the real solution once through the real loader; reuse the snapshot for every measurement ──
var msbuild = new MSBuildService(NullLogger<MSBuildService>.Instance);
var realLoader = new ProjectLoader(NullLogger<ProjectLoader>.Instance, msbuild);
using var realLoaded = await realLoader.LoadAsync(mainCsproj);
var project = realLoaded.Project;
var solution = realLoaded.Solution;
var compilation = await project.GetCompilationAsync()
    ?? throw new InvalidOperationException("Could not compile the target project.");

var sharedLoader = new SharedProjectLoader(solution, project);
var nav = new CodeNavigationService(NullLogger<CodeNavigationService>.Instance, sharedLoader);
var verification = new VerificationService(
    NullLogger<VerificationService>.Instance, DiagnosticComputationService.CompilerOnly);
var edit = new CodeEditService(
    NullLogger<CodeEditService>.Instance, sharedLoader, new DiffService(), verification);

Console.WriteLine($"Loaded project '{project.Name}' with {project.Documents.Count()} documents.");

// ── Tokenizer (cl100k_base, a GPT-4-class BPE — a documented proxy for Claude's tokenizer) ──
var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
int Tokens(string s) => tokenizer.CountTokens(s);
Measure M(string s) => new(s.Length, Tokens(s));

// The exact wire text a model reads. Every tool returns a ToolResult<T> envelope, and the MCP SDK
// renders it as ONE text content block via
//   JsonSerializer.Serialize(result, AIFunction.JsonSerializerOptions.GetTypeInfo(typeof(object)))
// (AIFunctionMcpServerTool) with McpJsonUtilities.DefaultOptions: camelCase Web defaults, nulls
// omitted, minified, System.Text.Json's default (non-relaxed) escaping — so `<`, `>`, `&`, `+`
// and non-ASCII arrive as \uXXXX escapes. Reproduce that serialization byte-for-byte here.
var wireJson = McpJsonUtilities.DefaultOptions;
string Ser<T>(T payload) =>
    JsonSerializer.Serialize(ToolResult<T>.Success(payload), wireJson.GetTypeInfo(typeof(object)));

// Tool-emitted file paths are solution-root-relative (falling back to the project directory when
// no .sln was loaded) — resolve them against that root, never the process cwd, so the benchmark
// produces identical numbers no matter where it is launched from.
var solutionRoot = Path.GetDirectoryName(solution.FilePath ?? project.FilePath)
    ?? throw new InvalidOperationException("Could not determine the solution root from the loaded solution.");

var fileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
string ReadFile(string path)
{
    var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(solutionRoot, path));
    if (!fileCache.TryGetValue(full, out var text))
    {
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Baseline read failed: '{path}' resolved to '{full}' (solution root '{solutionRoot}'), which does not exist.");
        }
        text = File.ReadAllText(full);
        fileCache[full] = text;
    }
    return text;
}
string Rel(string path) => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

TaskRow Row(string target, Measure whole, Measure? targeted, Measure tool)
{
    double vsWhole = whole.Tokens == 0 ? 0 : 1.0 - (double)tool.Tokens / whole.Tokens;
    double? vsTargeted = targeted is { Tokens: > 0 } ? 1.0 - (double)tool.Tokens / targeted.Tokens : null;
    return new TaskRow(target, whole, targeted, tool, vsWhole, vsTargeted);
}

// A parameter-qualified fully-qualified name the resolver accepts (disambiguates overloads).
var nameFmt = new SymbolDisplayFormat(
    globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
    memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

var suites = new List<SuiteResult>();

// ── Suite 1: search_symbols file outline vs. reading the whole file (systematic: every file) ──
Console.WriteLine("Suite 1: search_symbols (file outline)…");
var outlineRows = new List<TaskRow>();
foreach (var doc in project.Documents.Where(d => d.FilePath != null && IsRealSource(d.FilePath!)).OrderBy(d => d.FilePath))
{
    var whole = M(ReadFile(doc.FilePath!));
    var resp = await nav.SearchSymbolsAsync(TargetProject, null, doc.Name, null, 100_000);
    outlineRows.Add(Row(Rel(doc.FilePath!), whole, null, M(Ser(resp))));
}
suites.Add(Suite("outline", "search_symbols", "File outline instead of reading the file",
    "For every source file, compare the token cost of `search_symbols`'s structural outline against reading the whole file.",
    "Baseline B1 = the whole file. (No grep baseline — the outline *is* the compact form of a whole file.)", true, outlineRows));

// ── Enumerate the project's own declared symbols (types + members) for the symbol sweeps ──
var allSymbols = EnumerateBenchmarkable(compilation.Assembly.GlobalNamespace).ToList();

// ── Suites 2 & 3: get_symbol_info, without and with source, vs. the whole file (systematic) ──
Console.WriteLine($"Suites 2/3: get_symbol_info over {allSymbols.Count} symbols…");
var infoRows = new List<TaskRow>();
var infoSrcRows = new List<TaskRow>();
foreach (var sym in allSymbols)
{
    var file = sym.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
    if (file == null || !IsRealSource(file)) continue;
    var name = sym.ToDisplayString(nameFmt);
    var whole = M(ReadFile(file));
    var memberSource = sym.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()?.ToString() ?? "";
    var targeted = memberSource.Length > 0 ? M(memberSource) : null;

    try
    {
        var noSrc = await nav.GetSymbolInfoAsync(TargetProject, name, includeSource: false);
        infoRows.Add(Row(name, whole, targeted, M(Ser(noSrc))));
        var withSrc = await nav.GetSymbolInfoAsync(TargetProject, name, includeSource: true);
        infoSrcRows.Add(Row(name, whole, targeted, M(Ser(withSrc))));
    }
    catch
    {
        // Skip anything that fails to resolve unambiguously — keeps the sweep honest, not padded.
    }
}
suites.Add(Suite("symbol-info", "get_symbol_info", "Symbol metadata instead of reading the file",
    "For every declared type/member, compare `get_symbol_info` (metadata only, `includeSource=false`) against reading the whole containing file.",
    "B1 = whole file. B2 = just the symbol's own declaration source (grep-to-member model).", true, infoRows));
suites.Add(Suite("symbol-info-source", "get_symbol_info", "Go-to-definition (includeSource=true) — the honest weaker case",
    "Same symbols, but `includeSource=true` returns the declaration's source. Savings shrink (and can go negative for tiny members) because you get the body back — shown deliberately, not hidden.",
    "B1 = whole file. B2 = the symbol's own declaration source (this is roughly what the tool returns, so savings vs B2 hover near zero).", true, infoSrcRows));

// ── Suite 4: find_references vs. reading the referencing files (systematic sample) ──
Console.WriteLine("Suite 4: find_references…");
var refSample = allSymbols.Where(IsReferenceSample).ToList();
var refRows = new List<TaskRow>();
foreach (var sym in refSample)
{
    var name = sym.ToDisplayString(nameFmt);
    RoselineMCP.Models.ReferencesResponse resp;
    try { resp = await nav.FindReferencesAsync(TargetProject, name, includeDefinition: false, 100_000); }
    catch { continue; }
    if (resp.References.Count == 0) continue; // nothing to compare against

    try
    {
        var files = resp.References.Select(r => r.File).Distinct().ToList();
        var whole = M(string.Join("\n", files.Select(ReadFile)));
        var targeted = M(BuildGrepContext(resp.References, ReadFile));
        refRows.Add(Row($"{name} · {resp.References.Count} refs / {files.Count} files", whole, targeted, M(Ser(resp))));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  skipping find_references task for {name}: {ex.Message}");
    }
}
suites.Add(Suite("references", "find_references", "Reference list instead of reading every referencing file",
    "For each interface member and public service method, compare `find_references` against reading the files that contain those references.",
    "B1 = every referencing file, whole. B2 = the referencing lines ±3 lines (a `grep -C3` model).", true, refRows));

// ── Suite 5: find_implementations vs. reading candidate files (systematic: every interface) ──
Console.WriteLine("Suite 5: find_implementations…");
var implRows = new List<TaskRow>();
foreach (var iface in allSymbols.OfType<INamedTypeSymbol>().Where(t => t.TypeKind == TypeKind.Interface))
{
    var name = iface.ToDisplayString(nameFmt);
    RoselineMCP.Models.ImplementationsResponse resp;
    try { resp = await nav.FindImplementationsAsync(TargetProject, name, 100_000); }
    catch { continue; }
    if (resp.Implementations.Count == 0) continue;

    try
    {
        var files = resp.Implementations.Where(i => i.File != null).Select(i => i.File!).Distinct().ToList();
        if (files.Count == 0) continue;
        var whole = M(string.Join("\n", files.Select(ReadFile)));
        implRows.Add(Row($"{name} · {resp.Implementations.Count} impls", whole, null, M(Ser(resp))));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  skipping find_implementations task for {name}: {ex.Message}");
    }
}
suites.Add(Suite("implementations", "find_implementations", "Implementation list instead of reading candidate files",
    "For every interface, compare `find_implementations` against reading the files that declare the implementing types.",
    "B1 = each implementing type's file, whole.", true, implRows));

// ── Suite 6: get_call_graph (callers) vs. reading the caller files (sample of called methods) ──
Console.WriteLine("Suite 6: get_call_graph (callers)…");
var callRows = new List<TaskRow>();
foreach (var sym in allSymbols.OfType<IMethodSymbol>()
             .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility != Accessibility.Private))
{
    var name = sym.ToDisplayString(nameFmt);
    RoselineMCP.Models.CallGraphResponse resp;
    try { resp = await nav.GetCallGraphAsync(TargetProject, name, "callers", 1, 100_000); }
    catch { continue; }
    var nodes = resp.Callers ?? [];
    if (nodes.Count == 0) continue;

    try
    {
        var files = nodes.Where(n => n.File != null).Select(n => n.File!).Distinct().ToList();
        if (files.Count == 0) continue;
        var whole = M(string.Join("\n", files.Select(ReadFile)));
        callRows.Add(Row($"callers of {name} · {nodes.Count}", whole, null, M(Ser(resp))));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  skipping get_call_graph task for {name}: {ex.Message}");
    }
}
suites.Add(Suite("call-graph", "get_call_graph", "Caller list instead of reading the caller files",
    "For each non-private method that has callers, compare `get_call_graph` (direction=callers, depth=1) against reading the files that contain those callers.",
    "B1 = each caller's file, whole.", true, callRows));

// ── Suite 7: edit output — the tokens the agent EMITS to change code (illustrative, curated) ──
Console.WriteLine("Suite 7: edit output (rename_symbol / edit_member)…");
var editRows = new List<TaskRow>();

foreach (var (symbol, newName) in new[]
{
    ("RoselineMCP.Interfaces.IDiffService", "IUnifiedDiffService"),
    ("RoselineMCP.Services.SymbolResolver", "SymbolLookup"),
    ("RoselineMCP.Services.ProjectLoader", "WorkspaceProjectLoader"),
})
{
    try
    {
        var resp = await edit.RenameSymbolAsync(TargetProject, symbol, newName, previewOnly: true);
        if (resp.ChangedFiles.Count == 0) continue;
        var files = resp.ChangedFiles
            .Select(f => Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(solutionRoot, f)))
            .Where(File.Exists).ToList();
        if (files.Count == 0) continue;
        var whole = M(string.Join("\n", files.Select(ReadFile)));
        editRows.Add(Row($"rename {symbol} → {newName} · {resp.ChangedFiles.Count} files", whole, null, M(resp.Patch)));
    }
    catch { /* skip */ }
}
suites.Add(Suite("edit-output", "rename_symbol", "Emitting a diff instead of rewriting whole files",
    "Output-side saving: the tokens an agent must *emit*. Compare a `rename_symbol` unified diff against re-emitting the full text of every file it changes.",
    "B1 = the full text of each changed file (what you'd write to rewrite them). Illustrative curated cases, not a sweep.", false, editRows));

// ── Suite 8: check_compilation vs. real `dotnet build` stdout ──
// The only suite whose baseline is not source an agent reads but a COMMAND an agent runs, and the
// only one where the tool replaces a tool rather than a file read — so it is pooled separately
// from the navigation headline rather than folded into it.
Console.WriteLine("Suite 8: check_compilation vs dotnet build…");
var buildRows = new List<TaskRow>();
var buildFixtureRoot = Path.Combine(Path.GetTempPath(), "RoselineMCP.TokenBench_" + Guid.NewGuid().ToString("n"));

try
{
    foreach (var (label, files, expectManyErrors) in BuildComparisonFixtures())
    {
        var dir = Path.Combine(buildFixtureRoot, label);
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "Fixture.csproj");
        File.WriteAllText(csproj, FixtureCsproj());
        foreach (var (name, code) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), code);
        }

        // The baseline: exactly what `dotnet build` prints to an agent's terminal. No verbosity
        // flags — the default is what the agent actually sees, and pays for.
        var buildOutput = RunDotnetBuild(dir);
        if (buildOutput is null)
        {
            Console.Error.WriteLine($"  skipping {label}: dotnet build could not be run");
            continue;
        }

        using var fixtureLoaded = await realLoader.LoadAsync(csproj);

        // The shipped default (max: 20) — what a caller gets without asking for anything…
        var capped = await verification.VerifyAsync(null, fixtureLoaded.Solution);
        capped.ResolvedPath = fixtureLoaded.ResolvedPath;

        // …and untruncated, so the comparison is not flattered by truncation alone. On the worst
        // case these differ by an order of magnitude, and hiding that would turn the headline into
        // a statement about `max` rather than about the tool.
        var full = await verification.VerifyAsync(null, fixtureLoaded.Solution, int.MaxValue);
        full.ResolvedPath = fixtureLoaded.ResolvedPath;

        var errorCount = (full.Errors?.Count ?? 0) + full.Omitted;
        if (expectManyErrors && errorCount < 100)
        {
            Console.Error.WriteLine(
                $"  WARNING: {label} produced only {errorCount} errors — the worst case is not being exercised");
        }

        var buildMeasure = M(buildOutput);
        buildRows.Add(Row($"{label} · {errorCount} error(s) · default (max 20)", buildMeasure, null, M(Ser(capped))));
        buildRows.Add(Row($"{label} · {errorCount} error(s) · untruncated", buildMeasure, null, M(Ser(full))));
    }
}
finally
{
    try { Directory.Delete(buildFixtureRoot, recursive: true); } catch { /* best-effort cleanup */ }
}

suites.Add(Suite("check-compilation", "check_compilation", "A compile verdict instead of `dotnet build` output",
    "The edit loop's inner step. Compare `check_compilation`'s enveloped verdict against the stdout of a real `dotnet build` on the same broken state — one error, and the worst case of a rename that breaks hundreds of call sites.",
    "B1 = the full stdout of `dotnet build` at default verbosity, the text an agent actually reads. Both the shipped `max: 20` default and the untruncated output are reported, so the saving is not mistaken for an artifact of truncation.",
    false, buildRows));

// ── Headline: pool the clear navigation wins (exclude the includeSource weak case and edit output) ──
var headlineSuiteIds = new HashSet<string> { "outline", "symbol-info", "references", "implementations", "call-graph" };
var headlineRows = suites.Where(s => headlineSuiteIds.Contains(s.Id)).SelectMany(s => s.Rows).ToList();
long hWhole = headlineRows.Sum(r => (long)r.WholeFile.Tokens);
long hTool = headlineRows.Sum(r => (long)r.Tool.Tokens);
double pooled = hWhole == 0 ? 0 : 1.0 - (double)hTool / hWhole;
double medianAll = Median(headlineRows.Select(r => r.SavingsVsWholeFilePct).OrderBy(x => x).ToList());
var inv = CultureInfo.InvariantCulture;
var headline = new Headline(
    Math.Round(pooled, 4), Math.Round(medianAll, 4), hWhole, hTool,
    $"Across {headlineRows.Count} navigation tasks over RoselineMCP's own source, the read-only tools returned {hTool.ToString("N0", inv)} tokens where reading the corresponding files would take {hWhole.ToString("N0", inv)} — a pooled {pooled.ToString("P0", inv)} reduction (median per-task {medianAll.ToString("P0", inv)}).");

var report = new BenchmarkReport(
    new BenchmarkMetadata(
        DateTime.UtcNow.ToString("yyyy-MM-dd"),
        GitCommit(repoRoot),
        "RoselineMCP.sln",
        TargetProject,
        "cl100k_base (Microsoft.ML.Tokenizers, gpt-4) — a proxy for Claude's tokenizer",
        "MCP wire text: the ToolResult envelope serialized with the SDK's McpJsonUtilities.DefaultOptions (minified camelCase, default JSON escaping) — byte-identical to the text content block a model reads",
        outlineRows.Count,
        allSymbols.Count),
    Methodology(),
    Limitations(),
    headline,
    suites);

// ── Write the results next to the site's data dir, and print a summary ──
var outDir = Path.Combine(repoRoot, "website", "src", "data");
Directory.CreateDirectory(outDir);
var outPath = Path.Combine(outDir, "benchmark-results.json");
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
});
File.WriteAllText(outPath, json);

Console.WriteLine();
Console.WriteLine($"{"suite",-22} {"tool",-22} {"n",4}  {"median",7}  {"pooled",7}  {"weak",4}");
Console.WriteLine(new string('-', 74));
foreach (var s in suites)
{
    Console.WriteLine($"{s.Id,-22} {s.Tool,-22} {s.Aggregate.Count,4}  " +
        $"{s.Aggregate.MedianSavingsVsWholeFile,6:P0}  {s.Aggregate.PooledSavingsVsWholeFile,6:P0}  {s.Aggregate.WeakOrNegativeCount,4}");
}
Console.WriteLine(new string('-', 74));
Console.WriteLine($"HEADLINE: {headline.Statement}");
Console.WriteLine();
Console.WriteLine($"Wrote {outPath}");
return 0;

// ───────────────────────── helpers ─────────────────────────

SuiteResult Suite(string id, string tool, string title, string desc, string baselineNote, bool systematic, List<TaskRow> rows)
    => new(id, tool, title, desc, baselineNote, systematic, rows, Aggregate(rows));

static SuiteAggregate Aggregate(List<TaskRow> rows)
{
    if (rows.Count == 0)
        return new SuiteAggregate(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);

    var vsWhole = rows.Select(r => r.SavingsVsWholeFilePct).OrderBy(x => x).ToList();
    long tw = rows.Sum(r => (long)r.WholeFile.Tokens);
    long tt = rows.Sum(r => (long)r.Tool.Tokens);
    var withTargeted = rows.Where(r => r.SavingsVsTargetedPct != null).ToList();
    long ttw = withTargeted.Sum(r => (long)(r.Targeted?.Tokens ?? 0));
    long ttt = withTargeted.Sum(r => (long)r.Tool.Tokens);

    return new SuiteAggregate(
        rows.Count,
        rows.Count(r => r.SavingsVsWholeFilePct < 0.25),
        Round(Median(vsWhole)),
        Round(vsWhole.Average()),
        Round(vsWhole.Min()),
        Round(vsWhole.Max()),
        tw, tt,
        Round(tw == 0 ? 0 : 1.0 - (double)tt / tw),
        withTargeted.Count == 0 ? null : Round(Median(withTargeted.Select(r => r.SavingsVsTargetedPct!.Value).OrderBy(x => x).ToList())),
        withTargeted.Count == 0 ? null : Round(ttw == 0 ? 0 : 1.0 - (double)ttt / ttw));
}

static double Round(double v) => Math.Round(v, 4);

static double Median(List<double> sorted) =>
    sorted.Count == 0 ? 0
    : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
    : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

static IEnumerable<ISymbol> EnumerateBenchmarkable(INamespaceSymbol ns)
{
    foreach (var type in AllTypes(ns))
    {
        if (!type.Locations.Any(l => l.IsInSource)) continue;
        yield return type;
        foreach (var member in type.GetMembers())
        {
            if (!member.Locations.Any(l => l.IsInSource)) continue;
            if (member.IsImplicitlyDeclared || !member.CanBeReferencedByName) continue;
            if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.Protected or Accessibility.ProtectedOrInternal)) continue;
            if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
            if (member is INamedTypeSymbol) continue; // nested types are yielded by AllTypes
            yield return member;
        }
    }
}

static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
{
    foreach (var type in ns.GetTypeMembers())
    {
        yield return type;
        foreach (var nested in Nested(type)) yield return nested;
    }
    foreach (var child in ns.GetNamespaceMembers())
        foreach (var type in AllTypes(child)) yield return type;
}

static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
{
    foreach (var n in type.GetTypeMembers())
    {
        yield return n;
        foreach (var nn in Nested(n)) yield return nn;
    }
}

static bool IsReferenceSample(ISymbol s)
{
    if (s is INamedTypeSymbol) return false;
    var containingType = s.ContainingType;
    if (containingType == null) return false;
    if (containingType.TypeKind == TypeKind.Interface) return true; // all interface members
    return s.DeclaredAccessibility == Accessibility.Public
        && containingType.Name.EndsWith("Service", StringComparison.Ordinal)
        && s is IMethodSymbol { MethodKind: MethodKind.Ordinary };
}

static string BuildGrepContext(IReadOnlyList<RoselineMCP.Models.ReferenceLocation> references, Func<string, string> readFile)
{
    const int context = 3;
    var sb = new StringBuilder();
    foreach (var byFile in references.GroupBy(r => r.File))
    {
        var lines = readFile(byFile.Key).Replace("\r\n", "\n").Split('\n');
        var wanted = new SortedSet<int>();
        foreach (var r in byFile)
            for (var i = Math.Max(0, r.Line - 1 - context); i <= Math.Min(lines.Length - 1, r.Line - 1 + context); i++)
                wanted.Add(i);
        foreach (var i in wanted) sb.Append(lines[i]).Append('\n');
    }
    return sb.ToString();
}

static string GitCommit(string repoRoot)
{
    try
    {
        var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return string.IsNullOrEmpty(outp) ? "unknown" : outp;
    }
    catch { return "unknown"; }
}

static bool IsRealSource(string path)
{
    var p = path.Replace('\\', '/');
    return !p.Contains("/obj/") && !p.Contains("/bin/");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RoselineMCP.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static List<string> Methodology() =>
[
    "Every measured number is a real string. The tool output is the exact model-visible wire text: the ToolResult envelope serialized with the MCP SDK's own serializer (McpJsonUtilities.DefaultOptions — minified camelCase, default non-relaxed JSON escaping), byte-identical to the text content block the server emits over stdio. The baseline is the actual bytes of the source an agent would otherwise read.",
    "Tokens are counted with the cl100k_base BPE tokenizer (Microsoft.ML.Tokenizers, the gpt-4 encoding) — a documented, reproducible proxy for Claude's tokenizer, which is not published as a library. Character counts are included so nothing hinges on one tokenizer.",
    "Two baselines: B1 (whole-file) = the full text of the file(s) an agent must open to answer, matching how coding agents actually read. B2 (targeted) = only the relevant lines ±3 (a grep -C3 model), a conservative lower bound on savings.",
    "The headline pools clear navigation wins (outline, get_symbol_info metadata, find_references, find_implementations, get_call_graph). It excludes get_symbol_info includeSource=true (shown separately as the weaker case) and edit output (an output-token, not context, axis).",
    "The tools ran against RoselineMCP's own solution (dogfooding). The symbol and file suites are systematic sweeps over every candidate — not a hand-picked selection — so the distribution (min/median/mean/max) is representative, weak cases included.",
    "Pooled savings weight by size (1 − Σtool ÷ Σbaseline); median savings weight every task equally. Both are reported because they answer different questions.",
];

static List<string> Limitations() =>
[
    "The tokenizer is a proxy. Claude's exact counts differ, but code tokenizes similarly across modern BPE tokenizers, so the order of magnitude holds.",
    "Whole-file (B1) is the realistic baseline but the generous one: a disciplined agent that greps first lands nearer B2. Real agent behavior is between the two — both are shown.",
    "These tools save tokens on navigation and orientation. They do NOT remove the need to read code you are about to edit in depth; get_symbol_info(includeSource=true) makes that explicit.",
    "find_references / find_implementations baselines assume you read whole referencing files to be as complete as the tool (which searches the whole solution). If those references sit in large test files, the whole-file baseline is large — the targeted (B2) column keeps that honest.",
    "Results are specific to this codebase and its file sizes. A repo of tiny files saves less; a repo of large files saves more. Re-run on your own solution to get your numbers: dotnet run --project RoselineMCP.TokenBenchmark.",
];

/// <summary>A minimal SDK-style project with no PackageReferences, so `dotnet build` needs no restore feed.</summary>
static string FixtureCsproj() =>
    """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
      </PropertyGroup>
    </Project>
    """;

/// <summary>
/// The two cases: one error, and the mass-failure case. The second has the declaration already
/// renamed to <c>Helper2</c> while every caller still says <c>Helper</c> — the shape a careless
/// rename produces, and the one where `dotnet build` output is at its worst.
/// </summary>
static List<(string Label, List<(string Name, string Code)> Files, bool ExpectManyErrors)> BuildComparisonFixtures()
{
    var single = new List<(string, string)>
    {
        ("Program.cs", "public static class Program { public static void Main() { } }"),
        ("Broken.cs", "public class Broken { public int Nope() => Missing.Thing(); }"),
    };

    // 20 files x 15 call sites = 300 binding errors.
    var mass = new List<(string, string)>
    {
        ("Program.cs", "public static class Program { public static void Main() { } }"),
        ("Shared.cs", "public static class Helper2 { public static int Do() => 1; }"),
    };
    for (var f = 0; f < 20; f++)
    {
        var body = string.Join(
            "\n    ", Enumerable.Range(0, 15).Select(i => $"public int M{i}() => Helper.Do();"));
        mass.Add(($"Caller{f:D2}.cs", $"public class Caller{f:D2}\n{{\n    {body}\n}}"));
    }

    return
    [
        ("one-error", single, false),
        ("mass-failure-rename", mass, true),
    ];
}

/// <summary>
/// Runs a real <c>dotnet build</c> and returns its stdout — the baseline this suite measures
/// against. Returns <see langword="null"/> when the SDK cannot be invoked, so the suite is skipped
/// rather than reporting a fabricated baseline.
/// </summary>
static string? RunDotnetBuild(string projectDirectory)
{
    try
    {
        var psi = new ProcessStartInfo("dotnet", "build")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        if (process is null) return null;

        // Both pipes must be drained concurrently. Reading stdout to completion first lets a child
        // that fills the stderr buffer block on the write, stop producing stdout, and deadlock the
        // pair — unlikely for `dotnet build`, but the fix costs one line.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        stderr.GetAwaiter().GetResult();
        process.WaitForExit();
        return stdout;
    }
    catch
    {
        return null;
    }
}
