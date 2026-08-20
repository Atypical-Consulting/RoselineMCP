# RoselineMCP Architecture Documentation

## Overview

RoselineMCP is built on a layered architecture that separates concerns and promotes testability, maintainability, and extensibility. The system uses dependency injection throughout and follows SOLID principles.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────┐
│                    MCP Client (Claude, etc.)            │
└─────────────────┬───────────────────────────────────────┘
                  │ stdio (JSON-RPC)
┌─────────────────▼───────────────────────────────────────┐
│                    MCP Server Layer                      │
│  ┌──────────────────────────────────────────────────┐  │
│  │               Tool Registration                   │  │
│  │         (WithToolsFromAssembly())                │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────┬───────────────────────────────────────┘
                  │
┌─────────────────▼───────────────────────────────────────┐
│                    Tools Layer                          │
│  ┌──────────────────────────────────────────────────┐  │
│  │  AnalysisTools  │  [McpServerTool] Attributes   │  │
│  │  - AnalyzeSolution                               │  │
│  │  - ListDiagnostics                               │  │
│  │  - ApplyFixes                                    │  │
│  │  - CreatePatch                                   │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────┬───────────────────────────────────────┘
                  │ Dependency Injection
┌─────────────────▼───────────────────────────────────────┐
│                   Service Layer                         │
│  ┌──────────────────────────────────────────────────┐  │
│  │         Business Logic Services                  │  │
│  ├──────────────────────────────────────────────────┤  │
│  │  • SolutionAnalyzerService                       │  │
│  │  • CodeFixService                                │  │
│  │  • AnalyzerCatalog                               │  │
│  │  • DiagnosticComputationService                  │  │
│  │  • DiagnosticFilterService                       │  │
│  │  • CodeFixProviderFactory                        │  │
│  │  • PatchService / DiffService                    │  │
│  │  • MSBuildService                                │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────┬───────────────────────────────────────┘
                  │
┌─────────────────▼───────────────────────────────────────┐
│                   External Dependencies                 │
│  ┌──────────────────────────────────────────────────┐  │
│  │  • Roslyn (Microsoft.CodeAnalysis.*)             │  │
│  │  • MSBuild (Microsoft.Build.*)                   │  │
│  │  • Roslynator (Analyzers & CodeFixes)            │  │
│  │  • DiffPlex (Diff Generation)                    │  │
│  └──────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
```

## Core Components

### 1. Program.cs - Application Entry Point

Responsibilities:
- Host configuration and startup
- Service registration via DI
- MCP server configuration
- Global exception handling
- Logging configuration

Key features:
- Uses `Microsoft.Extensions.Hosting` for application lifecycle
- Configures stderr logging to avoid stdio conflicts
- Registers all services as singletons for performance
- Sets up environment-specific configurations

#### Process lifetime and shutdown

`Program.cs` ends its host builder with `.UseConsoleLifetime(...)`, but that is **not** what stops
the server in normal operation — it only handles SIGINT/SIGTERM. An MCP client that is finished
typically just closes the stdin pipe, and it is the **SDK's stdio transport** that ends the process
in that case: its read loop completes on EOF, shuts the server down, and unblocks `host.RunAsync()`,
which returns normally so the "stopped gracefully" path runs. Reading `UseConsoleLifetime` as the
whole lifetime story is the natural misreading, and it leads to the wrong conclusion that a client
which merely closes the pipe would strand a server.

Verified empirically on 2026-07-25 against `ModelContextProtocol` 1.4.1, which was the pinned version
at the time (the project has since moved to 2.2.0; this shutdown behavior has not been re-measured
against it), by driving the release binary over real pipes. All four paths exit with code 0:

| Path | Behaviour |
|---|---|
| EOF after a completed handshake | exits in ~0.8 s |
| EOF before any handshake (client dies on spawn) | exits in ~0.2 s |
| EOF while a tool call is in flight | drains the in-flight call, then exits (~2.3 s) — it does **not** linger to `DefaultTimeout` |
| stdin held open | stays alive, as it must — a live client holding stdin is not a leak |

⚠️ The in-flight row predates the write-confirmation gate and does **not** cover it. A write tool
parked in `ConfirmDestructiveWriteAsync` is waiting on a second clock
(`ConfirmDestructiveWritesTimeout`, 5 min by default — longer than `DefaultTimeout`), and whether
EOF frees that wait depends on the SDK cancelling the tool's request token on session teardown,
which this measurement never exercised. Treat the ~2.3 s figure as measured for an ordinary
analysis call only, until the confirmation path is re-measured.

This is what the README means by "exits when the client disconnects"; the claim is measured, not
assumed. A server still resident while its client holds stdin open is behaving correctly, so look
for the client that never exited rather than for a defect here.

### 2. Tool Layer

Location: `Tools/`

The tool layer provides the bridge between MCP protocol and internal services.

#### Tool Registration

Tools are automatically discovered using:
```csharp
services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

#### Tool Implementation Pattern

```csharp
[McpServerTool]
[Description("Tool description")]
public static async Task<string> ToolMethod(
    IService1 service1,  // Injected automatically
    IService2 service2,  // Injected automatically
    [Description("...")] string userParam1,
    [Description("...")] string? userParam2 = null)
{
    // Services are injected before user parameters
    // Return JSON response
}
```

### 3. Service Layer

Location: `Services/` and `Interfaces/`

#### Core Services

**SolutionAnalyzerService**
- Analyzes C# solutions and projects
- Collects diagnostics via `DiagnosticComputationService` (compiler + analyzers)
- Filters and aggregates results
- Manages MSBuildWorkspace lifecycle

**AnalyzerCatalog**
- Loads the Roslynator analyzer/fixer assemblies bundled in the `analyzers/` folder next to
  `RoselineMCP.dll` (the Roslynator packages are analyzer-asset-only — no `lib/` — so the csproj
  mirrors them into the build/publish/tool output)
- Instantiates every C#-supporting `DiagnosticAnalyzer` once (lazy, cached)
- Exposes the raw assemblies for `CodeFixProviderFactory` to scan

**DiagnosticComputationService**
- The single shared "diagnostics for this project" pass behind `AnalyzeSolution`,
  `ListDiagnostics`, and `ApplyFixes`
- Combines `Compilation.GetDiagnostics()` with `CompilationWithAnalyzers` over the bundled
  catalog plus the target project's own `AnalyzerReferences` (deduped by analyzer type)
- Per-analyzer exceptions are logged and skipped (`onAnalyzerException`); a failed analyzer pass
  degrades to compiler-only rather than failing the tool
- `RoselineMCP:RunAnalyzers = false` skips the **analyzer pass** entirely (compiler-only
  diagnostics). It does not stop **source generators**, which ship through the same
  `AnalyzerReferences` but run as part of building any `Compilation` rather than as part of this
  pass — so they execute on every semantic path, this one included. See `SECURITY.md`.

**VerificationService**
- Compiles a **candidate** `Solution` in memory and reports what the change did to the compiler's
  verdict — the gate behind `EditMember`/`RenameSymbol`/`ApplyFixes` and the entire payload of
  `CheckCompilation`
- **Scope** = the changed projects plus their *transitive dependents*, from
  `Solution.GetProjectDependencyGraph()`. File-only scope misses the cross-project breakage agents
  fail at most; whole-solution scope pays to compile projects the change cannot affect. The changed
  set is derived internally from `candidate.GetChanges(baseline)` and is deliberately not a
  parameter — a caller that under-reported it would silently narrow the scope and let the gate pass
  broken code
- **Delta** = a *multiset* difference over the position-insensitive key
  `(project, file, diagnostic id, message)`. Line and column are retained in the payload but never
  decide identity: a pre-existing `CS0103` at line 80 that a three-line edit above pushes to line 83
  is the same error, and a position-sensitive key would call it introduced *and* the original
  resolved, refusing a write for a break the edit never made. Multiset rather than set semantics so
  an edit that genuinely adds a *second* identical error still reports one
- Compiler diagnostics only, by design: production DI passes
  `DiagnosticComputationService.CompilerOnly`, because analyzers cost several times a bare compile
  and would turn a build gate into a style gate

**CodeFixService**
- Applies automated code fixes
- Manages code fix providers
- Generates before/after patches
- Handles multi-file fixes

**DiagnosticFilterService**
- Filters diagnostics by severity, ID, and file patterns
- Determines fixable diagnostics
- Project inclusion/exclusion logic
- Severity prioritization

**CodeFixProviderFactory**
- Dynamically loads code fix providers
- Caches provider instances
- Maps diagnostic IDs to fixers
- Supports Roslyn and Roslynator providers

**MSBuildService**
- Creates MSBuildWorkspace instances
- Manages MSBuild locator
- Handles workspace configuration
- Ensures proper cleanup

**PatchService / DiffService**
- Generates unified diff patches
- Line-by-line diff computation
- Supports side-by-side and inline diffs
- Handles large file comparisons

### 4. Model Layer

Location: `Models/`

Data transfer objects (DTOs) for structured communication:

- **Request Models**: Parameters for tool operations
- **Response Models**: Structured results from operations
- **Domain Models**: Internal representations of diagnostics, fixes, etc.

## Design Patterns

### Dependency Injection

All services are registered in the DI container and injected where needed:

```csharp
services.AddSingleton<ISolutionAnalyzerService, SolutionAnalyzerService>();
services.AddSingleton<ICodeFixService, CodeFixService>();
// ... etc
```

Benefits:
- Testability through interface mocking
- Loose coupling between components
- Easy service replacement/decoration
- Lifetime management

### Repository Pattern (Implicit)

Services act as repositories for their respective domains:
- `SolutionAnalyzerService` manages solution/project access
- `CodeFixProviderFactory` manages fix provider instances

### Factory Pattern

`CodeFixProviderFactory` implements the factory pattern for creating code fix providers:
- Lazy initialization
- Caching of instances
- Dynamic type loading

### Strategy Pattern

`DiagnosticFilterService` implements filtering strategies:
- Different filters for severity, IDs, files
- Composable filter chains
- Extensible filter types

## Data Flow

### Analyzing a Solution

```
1. MCP Client → AnalyzeSolution tool
2. Tool → SolutionAnalyzerService.AnalyzeSolutionAsync()
3. If pathOrGit is an http(s):// URL: shallow `git clone --depth 1` into a temp directory
   (deleted in a `finally` block once the operation completes)
4. Service → MSBuildService.CreateWorkspace()
5. Service → Load solution via MSBuildWorkspace
6. For each project, concurrently (bounded by the processor count):
   a. Get compilation
   b. Get diagnostics
   c. Filter via DiagnosticFilterService
   d. Collect an isolated per-project result (full severity counts + top candidates)
   Then merge the per-project results into the global summary and top-N selection
7. Return AnalyzeSolutionResponse
8. Tool → Serialize to JSON
9. MCP Server → Return to client
```

### Applying Fixes

```
1. MCP Client → ApplyFixes tool
2. Tool → CodeFixService.ApplyFixesAsync()
3. Service → Load project
4. Service → Get diagnostics for specified IDs
5. Service → CodeFixProviderFactory.GetFixProviders()
6. For each diagnostic:
   a. Find applicable fix provider
   b. Get code actions
   c. Apply changes to solution
7. Service → Generate unified diff
8. Service → Optionally save changes
9. Return ApplyFixesResponse
10. Tool → Serialize to JSON
11. MCP Server → Return to client
```

## Workspace Management

### MSBuildWorkspace Lifecycle

For `AnalyzeSolution`:

1. **Creation**: New workspace per operation
2. **Configuration**: Set up MSBuild properties
3. **Loading**: Load solution/project
4. **Operation**: Perform analysis/fixes
5. **Cleanup**: Dispose workspace

Every other project-loading tool (`ListDiagnostics`, `ApplyFixes`, and the navigation/edit tools)
loads through the shared `IProjectLoader` and reuses a cached workspace across calls via
`CachingProjectLoader` — see "Caching Strategies" under Performance Considerations below.

### Temporary Workspace Pattern

For safety, operations use temporary workspaces:
- Original files never modified directly
- Changes computed in memory
- Patches generated for review
- Explicit save operation required

## Error Handling Strategy

### Layered Error Handling

1. **Tool Layer**: every tool wraps its body in try/catch and delegates to the shared
   `ToolExecutionHelper` (`RoselineMCP/Tools/ToolExecutionHelper.cs`), which classifies any
   exception into a closed, stable `type` value and never lets one escape to the MCP protocol
   layer or leak a raw CLR exception message for unclassified failures
2. **Service Layer**: throws ordinary .NET exceptions (`FileNotFoundException`,
   `InvalidOperationException`, `TimeoutException`, ...) rather than a custom exception hierarchy
3. `ToolExecutionHelper` also owns cancellation/timeout composition: it links the caller's
   `CancellationToken` with the configurable `RoselineMCP:DefaultTimeout` (120,000 ms by default).
   On the three write tools that clock is armed only **after** any write-confirmation elicitation
   resolves, so `DefaultTimeout` is an analysis budget rather than a bound on the whole invocation;
   the human round-trip is bounded separately by `RoselineMCP:ConfirmDestructiveWritesTimeout`

### Error Response Format

Every tool returns the typed `ToolResult<T>` envelope (`{ ok, data, error }`); a failure is the
`ok: false` branch, with details nested under `error`:

```json
{
  "ok": false,
  "error": {
    "type": "ValidationError | NotFoundError | AnalysisError | CancelledError | TimeoutError | InternalError",
    "message": "Human-readable message (fixed, generic text for InternalError — never a raw exception message/stack trace)",
    "hint": "Optional, present on some ValidationError responses",
    "correlationId": "Always present — per-invocation GUID (see ToolInvocation.CorrelationId) that correlates a client-reported failure with the server-side log entry for that call"
  }
}
```

See [`docs/API.md`](API.md#error-handling) for the full closed set of `type` values and examples.

## Performance Considerations

### Caching Strategies

1. **Code Fix Provider Types**: `CodeFixProviderFactory` scans the fix-provider assemblies once
   per process (`_providersLoaded` flag) and caches the diagnostic-ID → provider-type mapping;
   individual `CodeFixProvider` *instances* are still created fresh per call via
   `Activator.CreateInstance`.
2. **MSBuild Location**: `MSBuildLocator` registration happens once per process
   (`MSBuildService._msBuildRegistered`, guarded by a lock).
3. **MSBuildWorkspace (AnalyzeSolution)**: intentionally **not** cached or reused — every
   `AnalyzeSolution` call creates and disposes its own `MSBuildWorkspace` (see "Workspace
   Isolation" below), trading some reload cost for isolation between calls.
4. **MSBuildWorkspace (IProjectLoader-backed tools — `ListDiagnostics`, `ApplyFixes`,
   navigation/edit)**: cached across calls — `IProjectLoader` resolves
   to `CachingProjectLoader`, a decorator over `ProjectLoader` that keeps up to 4 loaded
   workspaces (LRU-evicted, evicted workspaces disposed), keyed by the resolved `.sln`/`.csproj`
   path. Each entry stores a disk fingerprint — last-write-time + length of the `.sln`, every
   `.csproj`, and every document, plus the last-write-time of their containing directories (which
   catches added/removed files) — that is re-stat'd on every load; any mismatch disposes the
   cached workspace and reloads fresh. Roslyn `Solution` snapshots are immutable, so a cache hit
   can never observe another call's in-flight state, and RoselineMCP's own disk writes
   (`ApplyFixes`/`EditMember`/`RenameSymbol`) self-invalidate the entry on the next call. Disable
   with `RoselineMCP:WorkspaceCache = false` (every call then loads a fresh workspace, the
   pre-cache behavior).
5. **Verification baseline (`VerificationService`)**: per-project compiler-error sets, cached under
   a `SemaphoreSlim` with a small LRU bound (16 entries). Three properties of the key matter, each
   for a reason that was measured rather than assumed:
   - Keyed by the project's **file path** rather than its `ProjectId` — but not, as an earlier draft
     of this section claimed, because that makes entries survive a reload. Measured 2026-08-20
     against Roslyn 5.6.0: across a reload of the same project the path is stable while the
     `ProjectId` **and both version stamps** change, so an entry cached before a reload can never be
     hit after one, with either key. The path is simply the stable half of a key whose version half
     is what actually decides, and it keeps two projects from colliding. The hit this cache is really
     for is a repeat verification against the *same warm workspace* — the default `previewOnly` flow,
     where the baseline is re-verified on every edit while only the candidate changes.
   - Keyed by **both** `GetDependentSemanticVersionAsync()` and `GetDependentVersionAsync()`. The
     *semantic* version tracks consumable declarations and is blind to a method-body edit — and a
     body edit is precisely how a write introduces a compiler error. Keying on it alone would serve
     a stale baseline right after a write and blame the *next* edit for an error it did not cause.
     (`VerificationServiceCacheTests.A_Body_Only_Change_Misses_The_Cache` asserts both halves of
     that directly against Roslyn.)
   - Entries hold projected `DiagnosticDetail` values only — never `Diagnostic`, `Compilation` or
     `Location`, which would root a `SyntaxTree` into a workspace whose memory is never returned to
     the OS (see Memory Management below).

   Concurrent misses on the same project are single-flighted: the in-flight `Task` is stored under
   the gate, so two parallel `VerifyAsync` calls await one compilation. The cache serves the
   **baseline** side; the candidate side is always compiled, because Roslyn's version stamps are
   identity-based rather than content-based (measured — see `BENCHMARKS.md`).

### Parallel Project Analysis

Projects within a solution are analyzed **concurrently** — `AnalyzeSolution` runs the per-project
compilation/diagnostics work through `Parallel.ForEachAsync` bounded by
`Environment.ProcessorCount`. `Project.GetCompilationAsync`/`Compilation.GetDiagnostics` — and the
per-project `CompilationWithAnalyzers` analyzer pass (itself run with `concurrentAnalysis: true`)
— are safe to run in parallel across independent projects of one loaded solution. Each project writes its
result into its own slot (no shared mutable state across workers) and results are merged
afterwards, so the output is deterministic regardless of completion order; progress notifications
are emitted from a completed-project counter under a lock so the reported value strictly
increases, as MCP requires (the project named in each message follows completion order).

### Memory Management

- The diagnostics tools create and dispose a new `MSBuildWorkspace` per tool call (see "Workspace
  Management" above); the navigation/edit tools hold up to 4 cached workspaces in
  `CachingProjectLoader` (see "Caching Strategies" above), disposed on invalidation, LRU eviction,
  or host shutdown
- `ApplyFixes` re-fetches the project's compilation after every individual fix is applied, so
  later fixes see up-to-date source text/positions

#### Measured memory profile (2026-07-25)

The resident footprint of a running server was measured empirically against the release build, both
from outside the process (`ps -o rss`, driving the real binary over stdio) and from inside it
(`Process.WorkingSet64` alongside `GC.GetTotalMemory`). The numbers below are for
`RoselineMCP.sln` itself — a three-project solution — on .NET 10; a larger target scales the
per-workspace cost, not the shape of the result.

| State | Resident |
|---|---|
| Handshake complete, **zero tool calls** | **~78 MB** |
| After the first workspace-loading tool call | **~295 MB** |
| After further calls, then idle for 3 minutes | ~311 MB, flat to ±0.1 MB |

Two conclusions follow, and they are settled — do not re-derive them:

1. **The idle baseline is not the cache.** ~78 MB is reached *before any workspace exists*: it is
   the .NET runtime plus the Roslyn, MSBuild and Roslynator assembly set that this server must load
   to do its job. No cache policy can move it.
2. **Disposal does not return memory to the OS.** Disposing every cached workspace and then forcing
   a compacting gen-2 collection with LOH compaction moves the working set from 276 MB to 276 MB.
   At peak, only ~73 MB of a 274 MB working set is managed heap; the remaining majority is loaded
   assemblies, JIT-compiled code and metadata mappings, which are permanent for the process
   lifetime. Even a pass that halved the managed heap (65 → 38 MB) moved the working set by 5 MB.

Therefore **the entry bound is the only lever that has ever done anything**, and it is already
pulled: 4 entries, LRU-evicted, each evicted workspace disposed. Releasing cached workspaces after
an idle period was evaluated against these measurements and **rejected** — it would reclaim
essentially nothing while costing a full cold reload (~2 s versus ~0.02 s for a cache hit, a ~100×
first-call penalty) after every idle window, plus a disposal race against in-flight loads.

> [!IMPORTANT]
> **`RoselineMCP:WorkspaceCache = false` is not a memory-saving knob.** Disposing the workspace
> after every call is the most aggressive release policy possible, and it measures **worse**:
> ~374 MB versus ~296 MB after two calls (+26 %), with second-call latency going from 0.02 s to
> 0.92 s (~45×). The reload allocates on top of memory the previous disposal never returned. Treat
> the switch as an isolation/debugging control only.

An operator sizing a host should therefore budget for the *exercised* figure (~300 MB per server),
not the cold one — the gap between the two is the single most common source of surprise when many
MCP servers run side by side.

## Security Architecture

See [`SECURITY.md`](../SECURITY.md) and the README's [Security](../README.md#security) section
for the full, current picture — the summary below intentionally matches those rather than
restating an idealized version.

### Input Validation

- `include`/`exclude` (project name filter) and `files` (diagnostic file filter) are plain
  substring (`Contains`) matches — not regex, not glob.
- `AnalyzeSolutionTool` validates `severity` against a fixed whitelist
  (`Error`/`Warning`/`Info`/`Hidden`, case-insensitive) before calling the service.
- **No path-traversal-specific sanitization exists.** Solution/project paths are resolved with
  plain `File.Exists`/`Directory.Exists` checks, not canonicalized against an allowed root.
  `pathOrGit`, `project`, and `branch` should be treated as trusted operator input.

### Isolation

- Read-only by default: `AnalyzeSolution`, `ListDiagnostics`, and `CreatePatch` never write to
  disk; `ApplyFixes` defaults `previewOnly` to `true` at the MCP tool boundary.
- A fresh `MSBuildWorkspace` per operation for `AnalyzeSolution`. Every other project-loading
  tool (`ListDiagnostics`, `ApplyFixes`, navigation/edit) shares a fingerprint-invalidated
  workspace cache (`CachingProjectLoader`, see "Performance Considerations") — cached `Solution`
  snapshots are immutable and reloaded whenever anything changes on disk;
  `RoselineMCP:WorkspaceCache = false` disables the cache.
- **MSBuild is not a sandbox.** Loading a `.sln`/`.csproj` is a design-time MSBuild evaluation
  that can execute build logic embedded in the project (`<Exec>` tasks, custom `UsingTask`
  assemblies, imported `.targets`/`.props`). Analyzing a fully untrusted repository or Git URL
  carries a real code-execution risk on the host — RoselineMCP does not attempt to sandbox or
  disable MSBuild task execution during workspace loading. See `SECURITY.md` for operator
  recommendations.
- Git URLs (`http(s)://` only) are shallow-cloned (`git clone --depth 1`) into a temp directory
  that is deleted once the operation completes.

### Output Sanitization

- `InternalError`-class failures always return a fixed, generic message
  ("An unexpected internal error occurred. Check the server logs for details.") — the real
  exception message and stack trace are logged server-side only, never returned to the caller.
- Non-internal failures (validation, not-found, analysis, cancellation, timeout) return the
  exception's own message, classified into the closed `ToolErrorTypes` set rather than a raw CLR
  type name (see `docs/API.md#error-handling`).

## Extension Points

### Adding New Tools

1. Create tool class in `Tools/`
2. Add `[McpServerTool]` attribute
3. Implement tool logic
4. Auto-discovered at startup

### Adding New Services

1. Define interface in `Interfaces/`
2. Implement service in `Services/`
3. Register in DI container
4. Inject where needed

### Adding New Analyzers

Two paths, depending on whose analyzers they are:

1. **In the analyzed solution** — add the analyzer NuGet package to the *target* project;
   RoselineMCP picks it up from the project's `AnalyzerReferences` at analysis time (no
   RoselineMCP change needed). Fixes require a fixer RoselineMCP can load.
2. **Bundled with RoselineMCP** — reference the package in `RoselineMCP.csproj` with
   `GeneratePathProperty="true"` and add its `analyzers/dotnet/.../cs/*.dll` to the
   `RoslynatorAnalyzerAsset` item group so the DLLs land in the output `analyzers/` folder;
   `AnalyzerCatalog` discovers analyzers and `CodeFixProviderFactory` discovers fixers from
   there automatically.

## Configuration

### Environment Variables

- `ROSELINE_*`: Custom configuration (double underscore as section separator, e.g.
  `ROSELINE_RoselineMCP__EnableDiagnosticLogging=true`)
- `DOTNET_ENVIRONMENT`: Development/Production
- `DOTNET_*`: Runtime configuration

### Configuration Files

- `appsettings.json`: Base configuration, loaded from the install directory
  (`AppContext.BaseDirectory`) — never from the process working directory
- `appsettings.{Environment}.json`: Environment overrides (same directory)
- Command-line arguments: Highest priority

### Logging Configuration

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "RoselineMCP": "Debug",
      "Microsoft": "Warning"
    }
  }
}
```

## Testing Architecture

### Unit Test Structure

```
RoselineMCP.Tests/
├── Models/
│   └── DiagnosticDetailTests.cs
├── Services/
│   ├── SolutionAnalyzerServiceTests.cs (+ several focused companion files, e.g.
│   │   SolutionAnalyzerServiceGitCloneTests.cs, SolutionAnalyzerServiceWorkspaceTests.cs)
│   ├── CodeFixServiceTests.cs (+ CodeFixServiceIntegrationTests.cs, CodeFixServiceResolveTests.cs)
│   ├── CodeFixProviderFactoryTests.cs
│   ├── DiagnosticFilterServiceTests.cs
│   ├── DiffServiceTests.cs
│   ├── MSBuildServiceTests.cs
│   └── PatchServiceTests.cs
└── Tools/
    ├── AnalysisToolsTests.cs
    └── ToolErrorContractTests.cs  # cross-cutting: every tool maps exceptions to the
                                     # documented, closed ToolErrorTypes set (see docs/API.md)
```

### Test Patterns

- **Arrange-Act-Assert**: Standard test structure
- **Interface-based mocking**: [FakeItEasy](https://fakeiteasy.github.io/) fakes every service
  interface consumed by the tool layer
- **Assertions**: [Shouldly](https://github.com/shouldly/shouldly)
- **Test framework**: xUnit v3 (`xunit.v3` 4.x), hosted on Microsoft.Testing.Platform — the test
  project is an executable (`<OutputType>Exe</OutputType>`) that runs itself, with no VSTest
  bridge and no `Microsoft.NET.Test.Sdk`. `dotnet test` is put into MTP mode by `global.json`
- **Cross-cutting contract tests**: `ToolErrorContractTests` verifies every tool classifies a
  representative set of exceptions into the documented `ToolErrorTypes` values rather than
  leaking raw CLR exception type names

## Deployment Architecture

### Docker Support

Docker support is implemented today via a multi-stage [`Dockerfile`](../Dockerfile) (not a future
item): the build stage uses the `.NET 10.0` SDK image, and the runtime stage uses the Alpine-based
`.NET 10.0` runtime image, running as an unprivileged `roseline` user:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# ... restore, copy source, dotnet publish ...

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
RUN apk add --no-cache icu-libs
COPY --from=build /app/publish .
USER roseline
ENTRYPOINT ["dotnet", "RoselineMCP.dll"]
```

Multi-arch images (`linux/amd64`, `linux/arm64`) are built and pushed to Docker Hub and GHCR by the
`docker` job of `.github/workflows/release-please.yml`, when a release PR is merged — see
[`PUBLISH.md`](../PUBLISH.md) for the full release flow.

### CI/CD Pipeline

1. **CI** (`.github/workflows/ci.yml`): build + test with coverage on every push/PR to `main`/`dev`
   across Ubuntu, Windows, and macOS; enforces an 80% line-coverage threshold on Ubuntu
2. **CodeQL** (`.github/workflows/codeql.yml`): static security analysis
3. **Release** (`release-please.yml`, on every push to `dev`): release-please keeps a release PR up
   to date from the Conventional Commits since the last release; merging it tags `vX.Y.Z`, creates
   the GitHub Release, and gates three jobs on `release_created` — `publish` (NuGet via Trusted
   Publishing + the `.mcpb` bundle), `publish-registry` (the MCP Registry, after NuGet indexes the
   version) and `docker` (multi-arch to Docker Hub + GHCR). Publishing lives in this workflow
   rather than a tag-triggered one because GitHub does not fire `on: push: tags` for a
   `GITHUB_TOKEN`-created tag

## Monitoring and Diagnostics

### Logging Levels

- **Trace**: Detailed diagnostic info
- **Debug**: Development debugging
- **Information**: General flow
- **Warning**: Recoverable issues
- **Error**: Errors requiring attention
- **Critical**: System failures

### Health Checks (Future)

- Workspace availability
- MSBuild availability
- Memory usage monitoring
- Response time tracking

## Future Architecture Considerations

### Scalability

- Worker process pool for parallel analysis
- Distributed caching for large solutions
- Streaming results for massive outputs

### Extensibility

- Plugin architecture for custom analyzers
- Custom tool providers
- External service integrations

### Performance

- Incremental compilation caching
- Persistent workspace pools
- Optimized diff algorithms