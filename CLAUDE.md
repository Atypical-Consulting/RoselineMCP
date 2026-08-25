# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RoselineMCP** is a .NET 10.0 MCP (Model Context Protocol) server that provides comprehensive code analysis and automated fixing capabilities for C# solutions using Roslyn analyzers and code fix providers.

## Documentation Alignment (required)

**Whenever you change code, you MUST check that the documentation is still aligned — and update
whatever drifted in the same change.** A code change is not done until its docs match. This is a
hard rule; treat stale docs as a bug in the change.

When you add/remove a tool, or change a tool's parameters, response shape, annotations, or behavior,
review and update every surface that mirrors it:

- `docs/API.md` — per-tool request/response contract and the response-envelope section
- `CLAUDE.md` — the "MCP Tools Available" list and the "Adding New MCP Tools" example/pattern
- `README.md` — the tool list and any usage snippets
- `website/src/data/tools.ts` and `website/src/pages/tools.astro` — the public tools reference
  (name, title, kind, params, `data` payload, capability pills)
- `mcpb/manifest.json` — the `tools[]` array (names + descriptions)
- `CHANGELOG.md` — **nothing to edit by hand.** release-please generates each release's entries
  from Conventional Commits, so a user-facing change is described by your PR *title*; write it to
  read well as a changelog line. Expand it, if the one-liner is not enough, in the open release PR

For non-tool changes, still check the docs that describe what you touched (architecture notes,
security considerations, config, the release process below). When unsure whether a doc is affected,
grep for the symbol/behavior name across `docs/`, `README.md`, `CLAUDE.md`, and `website/`.

## Architecture

### Service Layer Architecture

The application uses a dependency injection-based service architecture with clear separation of concerns:

1. **Interface Contracts** (`Interfaces/`): All services have corresponding interfaces for testability
2. **Service Implementations** (`Services/`): Business logic separated by responsibility
   - `SolutionAnalyzerService`: Roslyn-based solution/project analysis
   - `CodeFixService`: Automated code fix application
   - `AnalyzerCatalog`: Loads the bundled Roslynator analyzer/fixer assemblies from the
     `analyzers/` folder next to RoselineMCP.dll (the packages are analyzer-asset-only, so the
     csproj mirrors them there at build time)
   - `DiagnosticComputationService`: The one shared "compiler + analyzer diagnostics" pass
     (`CompilationWithAnalyzers`) used by all three diagnostics tools — bundled catalog plus the
     target project's own analyzer references, deduped by analyzer type; disabled via
     `RoselineMCP:RunAnalyzers = false` (compiler-only). Returns a `DiagnosticComputationResult`:
     the diagnostics **and** an `AnalyzerLoadReport` naming every reference that contributed
     nothing. Roslyn reports a reference it cannot load (built against a newer
     `Microsoft.CodeAnalysis` than ours — the SDK's own NetAnalyzers are the universal case) by
     returning an *empty array* and raising `AnalyzerFileReference.AnalyzerLoadFailed`, never by
     throwing; the service subscribes to that event around each `GetAnalyzers` call and
     **remembers** a failure per reference object, because Roslyn raises it once and then serves
     the cached empty answer silently (the workspace cache hands the same references to every
     later call) under a per-reference lock (a solution's projects are analyzed in parallel).
     Reasons: `load-failure` (with Roslyn's `errorCode` — `ReferencesNewerCompiler` names both
     versions; a *partial* failure keeps the analyzers that loaded and is still named),
     `no C# analyzers` (generator-only, fixer-only and support assemblies — accurate, not
     alarming), `exception` (also the `(analyzer pass)` entry when the whole pass failed and the
     response fell back to compiler diagnostics). `analyzersRan: false` is the off state.
     `DescribeAnalyzerLoad(project)` yields the same report without a diagnostics pass — what
     `ApplyFixes` uses when none of its IDs had a fixer, so "no fixer" and "the reference carrying
     it never loaded" stay distinguishable
   - `VerificationService`: Compiles a candidate solution in memory and reports what the change did
     to the compiler's verdict — the gate behind the three write tools and the payload of
     `check_compilation`. Compiler-only by design (`DiagnosticComputationService.CompilerOnly`);
     caches per-project results keyed by **file path + both** the dependent semantic version and the
     dependent version (the semantic version alone is blind to method-body edits), storing detached
     `DiagnosticDetail` values only
   - `DiagnosticFilterService`: Filtering and categorization of diagnostics
   - `CodeFixProviderFactory`: Dynamic loading of Roslyn and Roslynator fix providers. Two layers:
     a **process-wide map** built once in the constructor (the Roslyn built-ins, then the
     `AnalyzerCatalog` assemblies — first-wins per ID), and a **per-project overlay** of the
     `CodeFixProvider` types carried by the target project's own `AnalyzerReferences`, reflected
     once per reference object (`ConditionalWeakTable`) through the reference's own
     `IAnalyzerAssemblyLoader` — so it adds no assembly the diagnostics pass does not already load.
     Lookup order is map first, overlay second: an ID both can fix resolves to the bundled
     provider. The `Project`-taking overloads (`GetProviderForDiagnostic(id, project)`,
     `GetFixableDiagnosticIds(project)`) are what `ApplyFixes` and `suggestedFixableIds` use; the
     no-project members are the `null` case. The decision is recorded in `SECURITY.md`
   - `PatchService`/`DiffService`: Unified diff generation for code changes
   - `MSBuildService`: MSBuildWorkspace management and initialization

3. **MCP Tool Layer** (`Tools/`): Static methods with `[McpServerTool]` attributes that bridge MCP protocol to services
4. **Model Layer** (`Models/`): DTOs for structured responses

### Key Architectural Patterns

- **Workspace Isolation (AnalyzeSolution)**: `AnalyzeSolution` creates a new MSBuildWorkspace per
  operation to prevent state pollution
- **Unified project loading**: `ListDiagnostics`, `ApplyFixes`, and all navigation/edit tools load
  their `project` through the single shared `IProjectLoader` (`ProjectLoader`) — one resolution
  behavior (auto-discovery, `.sln` support, exact-name project selection) everywhere
- **Workspace Cache (IProjectLoader-backed tools)**: `IProjectLoader` resolves to
  `CachingProjectLoader`, which reuses the loaded MSBuildWorkspace across tool calls. Each entry is
  fingerprinted (last-write-time + length of the `.sln`, every `.csproj`, every document, plus
  their directories' mtimes to catch added/removed files) and re-stat'd on every load — any change
  on disk disposes the cached workspace and reloads fresh, so RoselineMCP's own
  `ApplyFixes`/`EditMember`/`RenameSymbol` writes self-invalidate it. Bounded (4 entries, LRU);
  disable with `RoselineMCP:WorkspaceCache = false` to load a fresh workspace per call.
  ⚠️ That switch is for isolation/debugging only — **it does not save memory**. Measured
  2026-07-25: disposing per call costs +26% resident memory and ~45× second-call latency, because
  a disposed workspace's memory is never returned to the OS. Releasing cached workspaces on idle
  was measured and rejected for the same reason; do not re-propose it without reading
  `docs/ARCHITECTURE.md` § Memory Management first
- **Compile-verified writes**: the three write tools compile the candidate change in memory and
  **refuse** it when it introduces compiler errors (`applied: false`, nothing written, diff and
  errors returned). The gate is `introduced`, never `compiles` — an already-broken repository stays
  editable. Escape hatch: `allowIntroducedErrors: true`. Verification runs **before** the
  write-confirmation elicitation, so a human is never asked to approve a write that is about to be
  refused **or that carries no changes at all** — both checks live once in
  `ToolExecutionHelper.RunVerifiedWriteAsync`, which all three tools call
- **Compile guard (opt-in, `RoselineMCP:Guard`)**: the same verdict, applied to **every** file
  write rather than only RoselineMCP's own. `GuardEndpoint` (an `IHostedService`, registered only
  when the switch is on) serves a local Unix-domain socket; the `roseline-mcp guard` verb
  (`Guard/GuardClient.cs`) is the `PostToolUse` hook client that queries it and exits `2` with the
  report on stderr, or `0` and silent. `GuardService` keeps a per-**solution** Roslyn `Solution`
  snapshot and edits it **forward** from disk — it never reloads to build a baseline, because two
  independent loads share no lineage and `GetChanges` then reports every pre-existing error as
  introduced (measured: `introduced: 1, preexisting: 0` on two loads of identical broken code).
  It reports; it cannot block — `PostToolUse` has no blocking decision
- **Service Injection**: Tools receive services as first parameters via DI container
- **Typed Envelope**: Every tool returns a `ToolResult<T>` envelope (`{ ok, data, error }`) — the
  payload nested under `data` on success, error details under `error` on failure — and sets
  `UseStructuredContent = true` so the SDK also advertises an `outputSchema` and emits structured content.
  `error` carries `{ type, message, hint?, correlationId, resolvedPath? }`. `resolvedPath` names the
  absolute `.sln`/`.csproj` that answered, mirroring the success responses — the `.sln` when the
  project's solution was loaded and lists it, otherwise the `.csproj` opened directly (e.g. a
  project absent from its nearest ancestor `.sln`); it is **omitted — never
  `""` — when the failure happened before any project was resolved** (a bad argument rejected at the
  tool boundary, a load that itself failed), because "never resolved" is a different claim from
  "resolved to nothing". What decides presence is **when** the failure happened, not its `type`: an
  `InternalError` carries the path (only the *message* is scrubbed), so do a `TimeoutError` and a
  `ValidationError` the service raised after loading. The path travels from the service to
  `Error<T>`/`Cancellation<T>` on `Exception.Data` (`ResolvedPathStamp`) — the tool's `catch` block
  never sees the service's `loaded` handle. Every site that loads a project must stamp; a test pins
  the two counts together, since a missed site would drop the field silently
- **Error Resilience**: All tools return the failure envelope (`ok: false`) with error details, never
  throwing to the MCP layer
- **Streaming Prevention**: Stderr logging ensures clean stdio communication for MCP protocol

## Common Commands

### Build and Test
```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Run tests with coverage (writes RoselineMCP.Tests/TestResults/coverage.cobertura.xml)
dotnet test --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml \
  --results-directory RoselineMCP.Tests/TestResults

# Run a specific test
dotnet test --filter "FullyQualifiedName~SolutionAnalyzerServiceTests"

# Run tests in a specific project (note the --project flag: a bare path is not accepted)
dotnet test --project RoselineMCP.Tests/RoselineMCP.Tests.csproj
```

> `dotnet test` runs in **Microsoft.Testing.Platform (MTP) mode**, opted into by the `test`
> section of `global.json`. xunit.v3 4.x is MTP-only — it has no VSTest bridge — so VSTest
> options no longer apply: `--logger` is rejected outright (exit code 5), and the coverlet
> `-p:CollectCoverage=true` properties are accepted by MSBuild but silently do nothing, because
> MTP never invokes the `VSTest` target coverlet hooks into. Filters are the exception:
> `--filter "FullyQualifiedName~X"` still works through xunit's VSTest-syntax shim, alongside the
> native `--filter-class` / `--filter-method` / `--filter-trait` / `--filter-query`.

### Running the MCP Server
```bash
# Run in development mode
dotnet run --project RoselineMCP/RoselineMCP.csproj

# Run with specific environment
DOTNET_ENVIRONMENT=Development dotnet run --project RoselineMCP/RoselineMCP.csproj

# Watch mode for development
dotnet watch run --project RoselineMCP/RoselineMCP.csproj
```

### Package Management
```bash
# Restore all packages
dotnet restore

# Add a new package
dotnet add RoselineMCP/RoselineMCP.csproj package PackageName

# Update packages
dotnet list package --outdated
```

## MCP Tools Available

> The diagnostics tools (1–3) report compiler **and** analyzer diagnostics: the bundled
> Roslynator analyzers plus the target project's own analyzer references are executed via
> `CompilationWithAnalyzers` (`DiagnosticComputationService`). `RoselineMCP:RunAnalyzers = false`
> makes them compiler-only. All three carry an `analyzerLoad` block naming every analyzer
> reference that contributed nothing and why (`referencesConsulted`, `referencesContributing`,
> `analyzersLoaded`, `analyzersRan`, `notes[] { reference, reason, errorCode?, message? }`) — **omitted when every
> consulted reference contributed**, so an absent block means "nothing to report" and a present
> one always says something (`analyzersRan: false` when the pass is off). Degraded coverage is
> named, never silent.

### 1. AnalyzeSolution
Analyzes entire C# solutions for diagnostics with filtering options.
- **Parameters**: pathOrGit, branch, include, exclude, severity, maxDiagnostics
- **Returns**: Solution summary, project counts, top diagnostics with location details,
  `analyzerLoad` (merged across the analyzed projects: reference counters summed, `analyzersLoaded` the largest per-project count, each reference named once)

### 2. ListDiagnostics  
Gets detailed diagnostics for specific projects with statistics. Loads via `IProjectLoader`, so
`project` is **optional** (same auto-discovery and `.sln` support as the navigation tools).
- **Parameters**: project (optional), ids[], files[], max
- **Returns**: `resolvedPath` (the absolute `.sln`/`.csproj` actually loaded), diagnostics list, statistics by ID/severity, suggested fixable IDs (fixers from the Roslyn built-ins, the bundled catalog **and the project's own analyzer references** — bundled wins on a shared ID), `analyzerLoad`

### 3. ApplyFixes
Applies automated code fixes for specified diagnostic IDs. Loads via `IProjectLoader`, so
`project` is **optional** (same auto-discovery and `.sln` support as the navigation tools).
**Project-scoped**: a `.sln` target fixes its primary project only — the scope is enforced on the
write path (after every fix the working solution is rebuilt from the anchor project's changed
documents alone, carried by `DocumentId`, so what is verified is what is written — a linked file is
written from the anchor's copy regardless of project order) and reported in `notes[]` (which project
was fixed and which of the solution's were skipped — only when the caller's target was the solution,
never when they named a `.csproj` — plus any linked file whose write reaches a sibling), on every
path including preview.
- **Parameters**: ids[], project (optional), previewOnly, allowIntroducedErrors, max
- **Returns**: `resolvedPath` (the absolute `.sln`/`.csproj` actually loaded), changed files (relative to `resolvedPath`'s directory, forward slashes), unified diff patch, applied fixers list, `notes[]` (scope + per-ID status), `applied`, `verification`, `analyzerLoad` (from the first diagnostics pass — so "no diagnostics found for X" can be told apart from "the analyzer that reports X never loaded"). Fixers are resolved through the same three layers as `ListDiagnostics`' fixable IDs

### 14. CheckCompilation
Answers "does this compile right now, and what broke" against on-disk state — the replacement for a
`dotnet build` round trip in the edit loop. **Compiler diagnostics only** (unlike tools 1–3), so it
is fast enough for the inner loop; read-only.
- **Parameters**: project (optional), max (default 20)
- **Returns**: the verification verdict — `resolvedPath`, `compiles`, `errors[]`, `omitted`,
  `scope[]`, `scopeComplete`, `notes[]`

### 4. CreatePatch
Generates unified diff patches between text versions.
- **Parameters**: before, after, fileName
- **Returns**: Unified diff, line counts, change summary

### Code Navigation Tools (read-only, token-efficient)
These return precise structure instead of whole files (backed by `ICodeNavigationService` /
`CodeNavigationService`, which loads via `IProjectLoader`). All take an **optional** `project`
(name, directory, `.csproj` path, or `.sln` path); when omitted, RoselineMCP auto-discovers the
solution/project from its working directory, nearest level first — the cwd itself wins when it has
exactly one candidate, then each parent directory (up to 3) in order, then immediate
subdirectories — failing with an actionable message only when nothing is found or a single level
itself has multiple candidates. The containing solution is loaded when present, and symbol search/resolution spans
every project in it — a symbol declared only in a sibling project the anchor doesn't reference
(e.g. the Tests project) is still found, and references/renames span projects.

⚠️ **The working directory is the *server's*, fixed at spawn — not the agent's.** They diverge
whenever work happens in a git worktree (`.claude/worktrees/<name>`), which sits below the level
walk's reach, so an omitted `project` silently resolves the **main checkout**. Two checkouts of the
same repo are otherwise reported identically (same project name, same relative paths), so every
response from a tool with an optional `project` — tools 2, 3, 5–13 and 14 — carries **`resolvedPath`**,
the absolute `.sln`/`.csproj` that actually answered — the `.sln` when the solution was loaded and
contains the project, otherwise the `.csproj` that was opened directly (e.g. a project not listed in
its nearest ancestor `.sln`). Pass an absolute path as `project` to target a
specific checkout. (`AnalyzeSolution` is excluded: `pathOrGit` is required, so it never
auto-discovers.)

**Relative paths hang off `resolvedPath`.** The navigation tools' `file`/`definitionFile`, and
`ApplyFixes`/`EditMember`/`RenameSymbol`'s `changedFiles` **and unified-diff headers**, are
relativized against the directory of *that same response's* `resolvedPath` —
`LoadedProject.BaseDirectory`, the one authoritative anchor. The three services used to each
re-derive `Solution.FilePath ?? Project.FilePath`, which stopped agreeing with `resolvedPath` once
#151 fixed it (#181). Concretely: the solution's directory when the `.sln` answered, the project's
own when the `.csproj` did — including a project absent from its nearest ancestor `.sln`. So
`Path.GetDirectoryName(resolvedPath)` joined with such a path is a real file on disk, and a returned
`patch` applies from that directory rather than from the repo root.

⚠️ **Two things this does *not* cover**, so don't state the rule more widely than it holds:
`ListDiagnostics`/`AnalyzeSolution` report `file` as an **absolute** path (`DiagnosticDetail.File =
location.Path`), and `verification.errors[]`/`CheckCompilation`'s `errors[]` are still anchored by
`VerificationService.BaseDirectoryOf(Solution)` on the loaded solution — which diverges from
`resolvedPath` in exactly the unlisted-`.csproj` case. Closing the latter means threading the anchor
through `IVerificationService.VerifyAsync` from its five callers; it is deliberate follow-up work,
not an oversight.


That remedy covers **failures too**, and they are the common shape of this mistake: querying the
wrong checkout usually produces `NotFoundError: Symbol not found: 'X'`, not a wrong-but-successful
answer. The failure envelope's `error.resolvedPath` names the checkout that was searched, so
"the symbol is not there" stays distinguishable from "you asked the wrong tree".

- **5. SearchSymbols** — `project`, `query` (wildcard/substring), `file` (outline), `kinds[]`, `max`. Returns symbol summaries or a file outline.
- **6. GetSymbolInfo** — `project`, `symbol`, `includeSource`. Returns kind/modifiers/signature/baseTypes/interfaces/docs/definition (+ optional source); accessibility is inside `signature`, and empty/absent fields are omitted.
- **7. FindReferences** — `project`, `symbol`, `includeDefinition`, `max`. Returns use sites (file/line/snippet).
- **8. FindImplementations** — `project`, `symbol`, `max`. Returns implementations/overrides/derived types.
- **9. GetCallGraph** — `project`, `method`, `direction` (callers|callees|both), `depth` (1-3), `max`. Returns a cycle-safe call tree.
- **10. GetTypeHierarchy** — `project`, `type`, `direction` (base|derived|both). Returns base chain, interfaces, derived types.
- **13. GetSymbolAtPosition** — `project`, `file` (name or path suffix), `line` (1-based), `column` (optional, 1-based). Resolves a file:line(:column) — from a diagnostic, stack trace, grep, or `find_references` — to the symbol there (declared or referenced; line-only prefers declarations). Returns name/fullName/kind/signature/containingType/`isDeclaration`/definition location (+ optional docs); empty/absent fields are omitted.

### Code Editing Tools (write, `previewOnly` defaults to true)
Surgical edits (backed by `ICodeEditService` / `CodeEditService`, reusing `IDiffService`). Like
`ApplyFixes`, nothing is written unless `previewOnly: false` is passed explicitly. `project` is
optional here too (same auto-discovery and `.sln` support as the navigation tools).

Every write is **compile-verified** before it touches disk: the candidate change is compiled in
memory and refused when it introduces compiler errors (`applied: false`, nothing written, the diff
and the introduced errors returned). Pass `allowIntroducedErrors: true` to override, and `max`
(default 20) to bound the reported diagnostics.

- **11. EditMember** — `project`, `symbol`, `operation` (replace|add|delete), `newSource`, `previewOnly`, `allowIntroducedErrors`, `max`. Returns changed files + unified diff + `verification`.
- **12. RenameSymbol** — `project`, `symbol`, `newName`, `previewOnly`, `allowIntroducedErrors`, `max`. Roslyn solution-wide rename; returns changed files + unified diff + `verification`.

> `MoveMember` (moving a member between types) is intentionally **not** implemented — it is a
> refactor rather than a token-saver and is the highest-risk to get subtly wrong. Consider it a
> documented follow-up.

## Adding New MCP Tools

When implementing new tools:
1. Add method to appropriate tool class in `Tools/` with `[McpServerTool(UseStructuredContent = true)]`
2. Use `[Description("...")]` on method and all parameters for documentation
3. Accept required services as first parameters (injected automatically)
4. Return a `ToolResult<T>` envelope — the payload on success, a classified failure envelope on error
5. Handle exceptions and return the failure envelope (never throw to the MCP layer)
6. State one `Limitations:` clause and one `Example:` line in the `[Description]`, reusing
   `RoselineToolDescriptions.ProjectAutoDiscoveryLimit` when `project` is optional. Keep the whole
   description ≤ 165 words — `ToolDescriptionContractTests` enforces all three (and the tool count,
   so a new tool trips it until you opt it in deliberately)

Example:
```csharp
[McpServerTool(UseStructuredContent = true)]
[Description("Tool description — what it does, and when to prefer it over Read/Grep. "
    + "Limitations: the failure mode a caller cannot infer (capped by max, not atomic, preview-only, …)."
    + RoselineToolDescriptions.ProjectAutoDiscoveryLimit
    + " Example: new_tool{param:'value'} -> what comes back.")]
public static async Task<ToolResult<Result>> NewTool(
    IRequiredService service,
    [Description("Parameter description")] string param,
    ILoggerFactory? loggerFactory = null,
    McpServer? server = null,
    CancellationToken cancellationToken = default)
{
    using var invocation = ToolExecutionHelper.BeginInvocation(nameof(NewTool), loggerFactory, server);
    try
    {
        var result = await service.ProcessAsync(param, cancellationToken);
        invocation.MarkSuccess();
        return ToolResult<Result>.Success(result);
    }
    catch (Exception ex)
    {
        invocation.MarkFailure(ex.Message);
        // ToolExecutionHelper.Error<T> classifies the exception into the closed error-type set and
        // returns { ok: false, error: { type, message, correlationId, resolvedPath? } } —
        // never rethrowing. resolvedPath is read off ex.Data and stays absent when nothing resolved.
        return ToolExecutionHelper.Error<Result>(ex, invocation.CorrelationId, invocation.Logger);
    }
}
```

## Testing Strategy

### Unit Test Structure
- Tests located in `RoselineMCP.Tests/`
- Mirror the main project structure (Services/, Tools/)
- Use xUnit as the test framework
- Mock dependencies using interfaces

### Running Tests
```bash
# Run with detailed output
dotnet test --output Detailed

# Run with test filter
dotnet test --filter DisplayName~CodeFix

# Generate test reports (TRX for CI; xunit also ships html/junit/ctrf/xml reporters)
dotnet test --report-trx --report-trx-filename test-results.trx
dotnet test --report-xunit-html --report-xunit-html-filename test-results.html
```

## Dependencies

### Core MCP and Hosting
- **ModelContextProtocol** (2.2.0): MCP server implementation
- **Microsoft.Extensions.Hosting**: Application hosting and DI
- **Microsoft.Extensions.DependencyInjection**: Service registration

### Roslyn Analysis and Fixes
- **Microsoft.CodeAnalysis.CSharp**: Core Roslyn compiler
- **Microsoft.CodeAnalysis.Workspaces.MSBuild**: Solution/project loading
- **Microsoft.Build.Locator**: MSBuild resolution
- **Microsoft.CodeAnalysis.Features**: Code fix providers

### Analyzers and Rules
- **Roslynator.Analyzers**: Additional C# analyzers (RCS1xxx) + their fixers
- **Roslynator.CodeAnalysis.Analyzers**: Analyzers for Roslyn-API code (RCS9xxx)
- **Roslynator.CodeFixes**: Code fix providers for compiler (CS) diagnostics
- **Roslynator.Formatting.Analyzers**: Formatting rules (RCS0xxx, mostly disabled by default)

These four packages are **analyzer-asset-only** (no `lib/` folder), so RoselineMCP.csproj mirrors
their `analyzers/dotnet/roslyn4.7/cs/*.dll` into an `analyzers/` folder in the build/publish/tool
output (see the `RoslynatorAnalyzerAsset` item group). At runtime `AnalyzerCatalog` loads them
from there via `Assembly.LoadFrom`; nothing references them as ordinary lib dependencies.

### Utilities
- **DiffPlex**: Unified diff generation

### Testing
- **xunit.v3** (4.x): Test framework *and* runner — it hosts itself on Microsoft.Testing.Platform,
  which is why the test project sets `<OutputType>Exe</OutputType>` and why there is no
  `xunit.runner.visualstudio` and no `Microsoft.NET.Test.Sdk`
- **Microsoft.Testing.Extensions.CodeCoverage**: the single coverage producer (cobertura output)
- **Microsoft.Testing.Extensions.TrxReport**: TRX report for CI

> These two extension packages are versioned against `Microsoft.Testing.Platform` (2.3.3, pulled
> in by xunit.v3 4.x). Bump them together with xunit.v3, never one alone.

## Environment Configuration

The application supports environment-specific configuration through:
- `appsettings.json`: Base configuration, loaded from the install directory
  (`AppContext.BaseDirectory`, next to the binary) — never from the process working directory, so
  a target repository's own `appsettings.json` cannot reconfigure the server
- `appsettings.{Environment}.json`: Environment-specific overrides (same directory)
- `ROSELINE_` prefixed environment variables — double prefix for the `RoselineMCP` section, e.g.
  `ROSELINE_RoselineMCP__EnableDiagnosticLogging=true`
- Command-line arguments (highest precedence)

Configuration is read once at startup; no reload-on-change file watchers are registered.

The `RoselineMCP` section carries nine operator switches — `DefaultTimeout`,
`EnableDiagnosticLogging`, `WorkspaceCache`, `RunAnalyzers`, `ConfirmDestructiveWrites`,
`ConfirmDestructiveWritesTimeout`, `Guard`, `GuardEndpoint` and `GuardTimeout`. The
`ConfirmDestructiveWrites*` pair governs the write-confirmation elicitation. Leave
`ConfirmDestructiveWrites` `true` (the default) for interactive installs; set
`ROSELINE_RoselineMCP__ConfirmDestructiveWrites=false` on unattended hosts (CI, headless agents)
whose client can elicit but has no human to answer. `ConfirmDestructiveWritesTimeout` (default
`300000`, 5 minutes) bounds how long that prompt is waited on: on expiry the call returns a preview
with a note instead of writing, so an unanswered confirmation can no longer block a tool call
forever. It is deliberately a separate clock from `DefaultTimeout` — that is an analysis budget,
and human think-time must not be charged against it. `0` or less restores the unbounded wait.

The `Guard*` trio governs the **compile guard** (see Architecture). `Guard` is `false` by default;
setting it `true` makes the server open a local, per-user endpoint (`0600`) that the
`roseline-mcp guard` hook client queries. `GuardEndpoint` overrides the derived socket path.
`GuardTimeout` (default `10000`) bounds how long the *client* waits before giving up **silently** —
a third distinct clock, for the same reason as the one above: it bounds a hook the agent harness
will itself kill, not an analysis.

Logging levels adjust automatically:
- **Development**: Debug level for RoselineMCP namespace
- **Production**: Information level for RoselineMCP namespace

## Security Considerations

- **Read-only by default**: `ApplyFixes`' `previewOnly` parameter defaults to `true` at the MCP
  tool boundary — no file is written to disk unless the caller explicitly passes
  `previewOnly: false`. Behind that opt-in sits a *second*, best-effort guard: the write tools
  elicit a human confirmation before writing. It is best-effort by design (a client that cannot
  elicit still writes), but silence is not consent — an elicitation the client accepts and never
  answers expires after `RoselineMCP:ConfirmDestructiveWritesTimeout` (default 5 minutes) and
  downgrades the call to a preview rather than writing or hanging. An operator can disable the
  gate outright with `RoselineMCP:ConfirmDestructiveWrites = false` — after which the explicit
  `previewOnly: false` is the only thing standing between a tool call and a disk write. See
  `SECURITY.md`.
- **Compile-verified writes**: before any write tool touches disk, the candidate change is compiled
  in memory and refused if it introduces compiler errors. The guarantee is precise and deliberately
  narrow: **the verified change set compiles, and no refused edit is ever written** — *not* that the
  working tree always compiles after any outcome. Both multi-file writers apply changes file by
  file, so an interrupted rename leaves some files written and some not (covered by
  `CodeEditServiceTests.RenameSymbol_Multi_File_Write_Is_Not_Atomic`). `allowIntroducedErrors: true`
  waives the gate; `scopeComplete: false` says the gate could not see every dependent.
- **Real Git support**: `pathOrGit` accepts `http://`/`https://` Git URLs. RoselineMCP performs a
  shallow (`--depth 1`), read-only clone into a fresh temp directory using the system `git`
  executable, and deletes that directory once the operation finishes. Any other scheme (local
  path, `ssh://`, `git://`, `file://`, ...) is never treated as a Git remote.
- **MSBuild is not a sandbox**: loading a `.sln`/`.csproj` via `MSBuildWorkspace` is a design-time
  MSBuild evaluation, not a safe parse — it can execute build logic embedded in the project
  (`<Exec>` tasks, custom `UsingTask` assemblies, imported `.targets`/`.props`). Analyzing a fully
  untrusted repository or URL carries a real code-execution risk on the host running RoselineMCP.
  See `SECURITY.md` for the full write-up and operator recommendations.
- **Analyzer execution is code execution**: the diagnostics tools run Roslyn analyzers by default —
  the bundled Roslynator set *and* the target project's own analyzer references, which are
  third-party code executed in-process at analysis time. `RoselineMCP:RunAnalyzers = false`
  disables the **diagnostic analyzer** pass (compiler-only diagnostics). See `SECURITY.md`.
- **Code-fix providers are loaded from the project's `AnalyzerReferences` too — by decision.**
  The lookup adds no assembly the analyzer pass does not already load (each reference's assembly
  comes through its own `IAnalyzerAssemblyLoader`); it instantiates additional `CodeFixProvider`
  *types* from resident assemblies. `RunAnalyzers = false` does not govern it. Recorded in
  `SECURITY.md` so the choice stops being implicit.
- **Source generators run regardless of `RunAnalyzers`**: generators ship through the same
  `AnalyzerReferences` but execute as part of building *any* compilation, not as part of the
  diagnostics pass — so every semantic path runs them, including all navigation tools (via
  `SymbolResolver`), `ApplyFixes` and `AnalyzeSolution`. They cannot be suppressed without breaking
  semantic analysis: stripping `AnalyzerReferences` removes the generated types too, so every symbol
  resolving through generated code would report as a compile error. `RunAnalyzers = false` narrows
  the code-execution surface of an untrusted repository; isolation, not the switch, is the
  mitigation. See `SECURITY.md`.
- **`check_compilation` builds a compilation**, so it carries the same code-execution surface as
  every other semantic path: source generators shipped through the target project's
  `AnalyzerReferences` run as part of building it. It does *not* run diagnostic analyzers.
- **No dedicated path-traversal sanitization**: solution/project paths are resolved with plain
  `File.Exists`/`Directory.Exists` checks, not canonicalized against an allowed root. Treat
  `pathOrGit`, `project`, and `branch` as trusted operator input rather than sandboxed against
  arbitrary/hostile callers.
- `AnalyzeSolution` creates a fresh `MSBuildWorkspace` per operation (see "Workspace Isolation"
  above). Every other project-loading tool (`ListDiagnostics`/`ApplyFixes`/navigation/edit) loads
  through `IProjectLoader` and reuses a cached, read-only workspace across calls (see "Workspace
  Cache" above): Roslyn `Solution` snapshots are immutable, and the cache is invalidated by an
  on-disk fingerprint check on every call, so no stale state leaks between calls — including after
  `ApplyFixes`' own writes. Set `RoselineMCP:WorkspaceCache = false` to disable caching entirely.
- Changes from `ApplyFixes` are always returned as a unified diff patch in the response, in
  addition to (optionally) being written to disk.

## Releasing a New Version

Releases are cut by [release-please](https://github.com/googleapis/release-please)
(`.github/workflows/release-please.yml`). **There is no tag to push and no version to pick** — both
are derived from the Conventional Commits on `dev`. Full detail in `PUBLISH.md`.

1. **Land everything first** with Conventional Commit PR titles. The repo squash-merges, so the PR
   title *is* the commit release-please parses: the type selects the version bump (`feat:` → minor,
   `fix:` → patch, `!`/`BREAKING CHANGE` → major) and the changelog section. A bare title produces
   no release entry.
   ⚠️ **On a single-commit PR the commit message wins over the PR title** — the repo's squash title
   setting is `COMMIT_OR_PR_TITLE`, which only uses the PR title when there is more than one commit.
   Give the commit a conventional message too. Squash is now the *only* merge method enabled on the
   repository (`.github/repo-setup.yml`), so the squash premise is a mechanism rather than a
   convention — but that setting only guarantees the *squash*, never that the resulting subject is
   conventional, which is what the single-commit caveat above is about.
2. **release-please opens or updates a release PR** (`chore(dev): release X.Y.Z` — the scope is the
   target branch, not `main`) on every push to
   `dev`, carrying the version bump, the regenerated `CHANGELOG.md`, and the three JSON manifest
   version fields (`.mcp/server.json` ×2, `mcpb/manifest.json` — see `release-please-config.json`).
3. **Review the release PR.** Check the version it chose, and expand any generated changelog entry
   whose one-line commit subject loses something that mattered — it is an ordinary PR and editing it
   before merge is the intended workflow.
4. **Merge it. That is the release.** The same run tags `vX.Y.Z`, creates the GitHub Release, and
   then — gated on `release_created` — runs `publish` (pack → verify the packed `.mcp/server.json`
   → NuGet via Trusted Publishing → `.mcpb` → attach assets → trigger the docs rebuild),
   `publish-registry` (wait for NuGet to index → `mcp-publisher login github-oidc` → publish), and
   `docker` (multi-arch to Docker Hub + GHCR).
5. **Verify after the run**: NuGet has `X.Y.Z`, the GitHub Release exists with both assets, the MCP
   Registry entry shows `X.Y.Z`, the image is on both registries, and the docs `/releases` page
   lists it.

**Invariants (do not violate):**
- The MCP server name and the README `<!-- mcp-name: … -->` marker are **case-sensitive** and must
  match the GitHub org login exactly: `io.github.Atypical-Consulting/roseline-mcp`. A mismatch makes
  the registry publish `403`.
- `.mcp/server.json` must stay valid against the current published schema — check with
  `mcp-publisher validate .mcp/server.json` before releasing if you touched it.
- **Never** run `dotnet nuget push`, push a `vX.Y.Z` tag, or create the GitHub Release manually —
  release-please owns all three. A hand-pushed tag now fires *nothing*: the publishing jobs are
  gated on `release_created`, not on a tag.
- **Publishing must never be moved back to a tag-triggered workflow.** GitHub does not fire
  `on: push: tags` / `on: release` for a `GITHUB_TOKEN`-created tag, so such a workflow would never
  run again — silently. Pinned by `RoselineMCP.Tests/Release/ReleaseWorkflowTests.cs`.
- **`issues: write` must stay in the workflow's permissions.** release-please identifies a merged
  release PR solely by the `autorelease: pending` label and has to create it; without the
  permission the release PR merges and is never recognised — no tag, no Release, no publish, and no
  failed step.
- **The nuget.org Trusted Publishing policy names the workflow file** (`release-please.yml`).
  Renaming or replacing that workflow invalidates the policy and the push 403s *after* the tag and
  Release already exist.
- **Recover a failed publish with "Re-run failed jobs", never "Re-run all jobs"** — the latter
  re-runs release-please, which sees the release already created, reports `release_created: false`,
  and skips every publishing job.
- The registry entry is immutable per version: to change published metadata (e.g. `websiteUrl`),
  ship a new version.
- `CHANGELOG.md` has **no `## [Unreleased]` section** any more; release-please generates each
  release's entries. Do not reintroduce one — it would sit below the newest generated release and
  read as stale.