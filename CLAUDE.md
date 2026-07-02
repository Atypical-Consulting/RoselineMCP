# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RoselineMCP** is a .NET 10.0 MCP (Model Context Protocol) server that provides comprehensive code analysis and automated fixing capabilities for C# solutions using Roslyn analyzers and code fix providers.

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

- **Workspace Isolation**: Each operation creates a new MSBuildWorkspace to prevent state pollution
- **Service Injection**: Tools receive services as first parameters via DI container
- **Error Resilience**: All tools return JSON with error details on failure, never throwing to MCP layer
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
ASPNETCORE_ENVIRONMENT=Development dotnet run --project RoselineMCP/RoselineMCP.csproj

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

## Adding New MCP Tools

When implementing new tools:
1. Add method to appropriate tool class in `Tools/` with `[McpServerTool]` attribute
2. Use `[Description("...")]` on method and all parameters for documentation
3. Accept required services as first parameters (injected automatically)
4. Return JSON-serialized results or error responses
5. Handle exceptions and return structured error JSON

Example:
```csharp
[McpServerTool]
[Description("Tool description")]
public static async Task<string> NewTool(
    IRequiredService service,
    [Description("Parameter description")] string param)
{
    try
    {
        var result = await service.ProcessAsync(param);
        return JsonSerializer.Serialize(result);
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { error = ex.Message, type = ex.GetType().Name });
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
- `appsettings.json`: Base configuration
- `appsettings.{Environment}.json`: Environment-specific overrides
- `ROSELINE_` prefixed environment variables
- Command-line arguments

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
- Each operation creates a fresh `MSBuildWorkspace` (see "Workspace Isolation" above) — no
  workspace state or MSBuild-loaded solution is shared or cached across calls.
- Changes from `ApplyFixes` are always returned as a unified diff patch in the response, in
  addition to (optionally) being written to disk.