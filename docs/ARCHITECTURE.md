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
- Collects diagnostics from Roslyn compilation
- Filters and aggregates results
- Manages MSBuildWorkspace lifecycle

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
6. For each project, sequentially (not in parallel):
   a. Get compilation
   b. Get diagnostics
   c. Filter via DiagnosticFilterService
   d. Aggregate results
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

1. **Creation**: New workspace per operation
2. **Configuration**: Set up MSBuild properties
3. **Loading**: Load solution/project
4. **Operation**: Perform analysis/fixes
5. **Cleanup**: Dispose workspace

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
   `CancellationToken` with the configurable `RoselineMCP:DefaultTimeout` (120,000 ms by default)

### Error Response Format

```json
{
  "error": "Human-readable message (fixed, generic text for InternalError — never a raw exception message/stack trace)",
  "type": "ValidationError | NotFoundError | AnalysisError | CancelledError | TimeoutError | InternalError",
  "hint": "Optional, present on some ValidationError responses",
  "correlationId": "Always present — per-invocation GUID (see ToolInvocation.CorrelationId) that correlates a client-reported failure with the server-side log entry for that call"
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
3. **MSBuildWorkspace**: intentionally **not** cached or reused — every `AnalyzeSolution`,
   `ListDiagnostics`, and `ApplyFixes` call creates and disposes its own `MSBuildWorkspace` (see
   "Workspace Isolation" below), trading some reload cost for isolation between calls.

### Sequential Processing

Projects within a solution are analyzed **sequentially**, not concurrently — `AnalyzeSolution`
loops over `solution.Projects` with a plain `foreach`. Diagnostics for each project are then
filtered in-process against the compilation the workspace already produced. This keeps
`MSBuildWorkspace` state predictable per call; parallelizing the project loop is tracked as a
possible future optimization, not current behavior.

### Memory Management

- A new `MSBuildWorkspace` is created and disposed per tool call (see "Workspace Management"
  above) rather than pooled or shared
- `ApplyFixes` re-fetches the project's compilation after every individual fix is applied, so
  later fixes see up-to-date source text/positions

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
- A fresh `MSBuildWorkspace` per operation — no shared/cached workspace state across calls.
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

1. Add analyzer NuGet package
2. Automatically discovered by Roslyn
3. Fix providers loaded dynamically

## Configuration

### Environment Variables

- `ROSELINE_*`: Custom configuration
- `ASPNETCORE_ENVIRONMENT`: Development/Production
- `DOTNET_*`: Runtime configuration

### Configuration Files

- `appsettings.json`: Base configuration
- `appsettings.{Environment}.json`: Environment overrides
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
- **Test framework**: xUnit v3 (`xunit.v3` + `xunit.runner.visualstudio`)
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

Multi-arch images (`linux/amd64`, `linux/arm64`) are built and pushed to Docker Hub and GHCR by
`.github/workflows/docker-publish.yml` on every `v*` tag push — see [`PUBLISH.md`](../PUBLISH.md)
for the full release flow.

### CI/CD Pipeline

1. **CI** (`.github/workflows/ci.yml`): build + test with coverage on every push/PR to `main`/`dev`
   across Ubuntu, Windows, and macOS; enforces an 80% line-coverage threshold on Ubuntu
2. **CodeQL** (`.github/workflows/codeql.yml`): static security analysis
3. **Release** (on `v*` tag push): `publish-nuget.yml` packs and pushes to nuget.org;
   `docker-publish.yml` builds and pushes the multi-arch container — both run in parallel,
   triggered by the same tag

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