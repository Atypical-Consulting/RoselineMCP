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
- `CHANGELOG.md` — add an entry under `## [Unreleased]` for any user-facing change

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
   - `DiagnosticFilterService`: Filtering and categorization of diagnostics
   - `CodeFixProviderFactory`: Dynamic loading of Roslyn and Roslynator fix providers
   - `PatchService`/`DiffService`: Unified diff generation for code changes
   - `MSBuildService`: MSBuildWorkspace management and initialization

3. **MCP Tool Layer** (`Tools/`): Static methods with `[McpServerTool]` attributes that bridge MCP protocol to services
4. **Model Layer** (`Models/`): DTOs for structured responses

### Key Architectural Patterns

- **Workspace Isolation (diagnostics tools)**: `AnalyzeSolution`/`ListDiagnostics`/`ApplyFixes`
  create a new MSBuildWorkspace per operation to prevent state pollution
- **Workspace Cache (navigation/edit tools)**: everything backed by `IProjectLoader` resolves to
  `CachingProjectLoader`, which reuses the loaded MSBuildWorkspace across tool calls. Each entry is
  fingerprinted (last-write-time + length of the `.sln`, every `.csproj`, every document, plus
  their directories' mtimes to catch added/removed files) and re-stat'd on every load — any change
  on disk disposes the cached workspace and reloads fresh, so RoselineMCP's own
  `ApplyFixes`/`EditMember`/`RenameSymbol` writes self-invalidate it. Bounded (4 entries, LRU);
  disable with `RoselineMCP:WorkspaceCache = false` to load a fresh workspace per call
- **Service Injection**: Tools receive services as first parameters via DI container
- **Typed Envelope**: Every tool returns a `ToolResult<T>` envelope (`{ ok, data, error }`) — the
  payload nested under `data` on success, error details under `error` on failure — and sets
  `UseStructuredContent = true` so the SDK also advertises an `outputSchema` and emits structured content
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

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run a specific test
dotnet test --filter "FullyQualifiedName~SolutionAnalyzerServiceTests"

# Run tests in a specific project
dotnet test RoselineMCP.Tests/RoselineMCP.Tests.csproj
```

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

### 1. AnalyzeSolution
Analyzes entire C# solutions for diagnostics with filtering options.
- **Parameters**: pathOrGit, branch, include, exclude, severity, maxDiagnostics
- **Returns**: Solution summary, project counts, top diagnostics with location details

### 2. ListDiagnostics  
Gets detailed diagnostics for specific projects with statistics.
- **Parameters**: project, ids[], files[], max
- **Returns**: Diagnostics list, statistics by ID/severity, suggested fixable IDs

### 3. ApplyFixes
Applies automated code fixes for specified diagnostic IDs.
- **Parameters**: project, ids[], previewOnly
- **Returns**: Changed files, unified diff patch, applied fixers list

### 4. CreatePatch
Generates unified diff patches between text versions.
- **Parameters**: before, after, fileName
- **Returns**: Unified diff, line counts, change summary

### Code Navigation Tools (read-only, token-efficient)
These return precise structure instead of whole files (backed by `ICodeNavigationService` /
`CodeNavigationService`, which loads via `IProjectLoader`). All take an **optional** `project`
(name, directory, `.csproj` path, or `.sln` path); when omitted, RoselineMCP auto-discovers the
solution/project from its working directory (searching the cwd, a few parent directories, and
immediate subdirectories, and failing with an actionable message only when the match is empty or
ambiguous). The containing solution is loaded when present, and symbol search/resolution spans
every project in it — a symbol declared only in a sibling project the anchor doesn't reference
(e.g. the Tests project) is still found, and references/renames span projects.

- **5. SearchSymbols** — `project`, `query` (wildcard/substring), `file` (outline), `kinds[]`, `max`. Returns symbol summaries or a file outline.
- **6. GetSymbolInfo** — `project`, `symbol`, `includeSource`. Returns kind/modifiers/signature/baseTypes/interfaces/docs/definition (+ optional source); accessibility is inside `signature`, and empty/absent fields are omitted.
- **7. FindReferences** — `project`, `symbol`, `includeDefinition`, `max`. Returns use sites (file/line/snippet).
- **8. FindImplementations** — `project`, `symbol`, `max`. Returns implementations/overrides/derived types.
- **9. GetCallGraph** — `project`, `method`, `direction` (callers|callees|both), `depth` (1-3), `max`. Returns a cycle-safe call tree.
- **10. GetTypeHierarchy** — `project`, `type`, `direction` (base|derived|both). Returns base chain, interfaces, derived types.

### Code Editing Tools (write, `previewOnly` defaults to true)
Surgical edits (backed by `ICodeEditService` / `CodeEditService`, reusing `IDiffService`). Like
`ApplyFixes`, nothing is written unless `previewOnly: false` is passed explicitly. `project` is
optional here too (same auto-discovery and `.sln` support as the navigation tools).

- **11. EditMember** — `project`, `symbol`, `operation` (replace|add|delete), `newSource`, `previewOnly`. Returns changed files + unified diff.
- **12. RenameSymbol** — `project`, `symbol`, `newName`, `previewOnly`. Roslyn solution-wide rename; returns changed files + unified diff.

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

Example:
```csharp
[McpServerTool(UseStructuredContent = true)]
[Description("Tool description")]
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
        // returns { ok: false, error: { type, message, correlationId } } — never rethrowing.
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
dotnet test --logger "console;verbosity=detailed"

# Run with test filter
dotnet test --filter DisplayName~CodeFix

# Generate test report
dotnet test --logger html
```

## Dependencies

### Core MCP and Hosting
- **ModelContextProtocol** (1.4.0): MCP server implementation
- **Microsoft.Extensions.Hosting**: Application hosting and DI
- **Microsoft.Extensions.DependencyInjection**: Service registration

### Roslyn Analysis and Fixes
- **Microsoft.CodeAnalysis.CSharp**: Core Roslyn compiler
- **Microsoft.CodeAnalysis.Workspaces.MSBuild**: Solution/project loading
- **Microsoft.Build.Locator**: MSBuild resolution
- **Microsoft.CodeAnalysis.Features**: Code fix providers

### Analyzers and Rules
- **Roslynator.Analyzers**: Additional C# analyzers
- **Roslynator.CodeFixes**: Code fix providers
- **Roslynator.Formatting.Analyzers**: Formatting rules

### Utilities
- **DiffPlex**: Unified diff generation

### Testing
- **xunit**: Test framework
- **xunit.runner.visualstudio**: Test runner
- **Microsoft.NET.Test.Sdk**: Test SDK

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

Logging levels adjust automatically:
- **Development**: Debug level for RoselineMCP namespace
- **Production**: Information level for RoselineMCP namespace

## Security Considerations

- **Read-only by default**: `ApplyFixes`' `previewOnly` parameter defaults to `true` at the MCP
  tool boundary — no file is written to disk unless the caller explicitly passes
  `previewOnly: false`.
- **Real Git support**: `pathOrGit` accepts `http://`/`https://` Git URLs. RoselineMCP performs a
  shallow (`--depth 1`), read-only clone into a fresh temp directory using the system `git`
  executable, and deletes that directory once the operation finishes. Any other scheme (local
  path, `ssh://`, `git://`, `file://`, ...) is never treated as a Git remote.
- **MSBuild is not a sandbox**: loading a `.sln`/`.csproj` via `MSBuildWorkspace` is a design-time
  MSBuild evaluation, not a safe parse — it can execute build logic embedded in the project
  (`<Exec>` tasks, custom `UsingTask` assemblies, imported `.targets`/`.props`). Analyzing a fully
  untrusted repository or URL carries a real code-execution risk on the host running RoselineMCP.
  See `SECURITY.md` for the full write-up and operator recommendations.
- **No dedicated path-traversal sanitization**: solution/project paths are resolved with plain
  `File.Exists`/`Directory.Exists` checks, not canonicalized against an allowed root. Treat
  `pathOrGit`, `project`, and `branch` as trusted operator input rather than sandboxed against
  arbitrary/hostile callers.
- The diagnostics tools (`AnalyzeSolution`/`ListDiagnostics`/`ApplyFixes`) create a fresh
  `MSBuildWorkspace` per operation (see "Workspace Isolation" above). The navigation/edit tools
  reuse a cached, read-only workspace across calls (see "Workspace Cache" above): Roslyn `Solution`
  snapshots are immutable, and the cache is invalidated by an on-disk fingerprint check on every
  call, so no stale state leaks between calls. Set `RoselineMCP:WorkspaceCache = false` to disable
  caching entirely.
- Changes from `ApplyFixes` are always returned as a unified diff patch in the response, in
  addition to (optionally) being written to disk.

## Releasing a New Version

A release is cut by pushing a `vX.Y.Z` git tag; the `Publish NuGet` workflow
(`.github/workflows/publish-nuget.yml`) does everything else. **Follow these steps exactly every
time** so releases are identical across sessions. Do them in order; do not skip or reorder.

1. **Land everything first.** All intended changes must be merged into `dev`, and `dev` must be
   green (CI passing, no open blocking PRs).
2. **Pick the version** by semver against the *shipped tool contract*:
   - **patch** (`Z`) — bug fixes, dependency bumps, packaging/CI/docs only (no tool behavior change)
   - **minor** (`Y`) — new tools or backward-compatible tool features
   - **major** (`X`) — breaking changes to a tool's wire shape / parameters
3. **Roll the CHANGELOG** (`CHANGELOG.md`): rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD`
   (today's date) and add a fresh empty `## [Unreleased]` above it.
4. **Bump the checked-in version defaults to `X.Y.Z`** so they never drift (CI re-stamps them from
   the tag at publish, but keep them consistent):
   - `.mcp/server.json` → both `version` **and** `packages[0].version`
   - `mcpb/manifest.json` → `version`
5. **Commit** the prep as `chore(release): X.Y.Z — <summary>` and **push to `dev`**.
6. **Tag and push**: `git tag -a vX.Y.Z -m "vX.Y.Z - <summary>" <commit>` then
   `git push origin vX.Y.Z`. This is the only manual trigger.
7. **The tag runs `publish-nuget.yml` automatically — never do these by hand:**
   - `publish` job: build → test → pack → **verify the packed `.mcp/server.json`** (fails if the
     manifest is missing or its version ≠ tag) → push to NuGet.org → build `RoselineMCP.mcpb` →
     create the GitHub Release (attaches `.nupkg` + `.mcpb`, notes from the CHANGELOG section).
   - `publish-registry` job: waits for NuGet to index the version → `mcp-publisher login
     github-oidc` (no secret) → publishes `.mcp/server.json` to `registry.modelcontextprotocol.io`.
   - `deploy-docs` then rebuilds the site via a `workflow_run` trigger, so the `/releases` page
     picks up the new release.
8. **Verify after the run**: NuGet has `X.Y.Z`, the GitHub Release exists with both assets, the MCP
   Registry entry shows `X.Y.Z`, and the docs `/releases` page lists it.

**Invariants (do not violate):**
- The MCP server name and the README `<!-- mcp-name: … -->` marker are **case-sensitive** and must
  match the GitHub org login exactly: `io.github.Atypical-Consulting/roseline-mcp`. A mismatch makes
  the registry publish `403`.
- `.mcp/server.json` must stay valid against the current published schema — check with
  `mcp-publisher validate .mcp/server.json` before releasing if you touched it.
- **Never** run `dotnet nuget push` or create the GitHub Release manually — the workflow owns them
  (idempotent; re-pushing the same tag heals rather than duplicates).
- The registry entry is immutable per version: to change published metadata (e.g. `websiteUrl`),
  ship a new version.