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
3. Service → MSBuildService.CreateWorkspace()
4. Service → Load solution via MSBuildWorkspace
5. For each project:
   a. Get compilation
   b. Get diagnostics
   c. Filter via DiagnosticFilterService
   d. Aggregate results
6. Return AnalyzeSolutionResponse
7. Tool → Serialize to JSON
8. MCP Server → Return to client
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

1. **Tool Layer**: Catches all exceptions, returns JSON error
2. **Service Layer**: Throws domain-specific exceptions
3. **External Layer**: Wraps external library exceptions

### Error Response Format

```json
{
  "error": "Human-readable message",
  "type": "ExceptionTypeName",
  "details": { /* optional context */ }
}
```

## Performance Considerations

### Caching Strategies

1. **Code Fix Providers**: Cached after first load
2. **MSBuild Location**: Located once at startup
3. **Compilation Results**: Reused within operations

### Parallel Processing

- Projects analyzed concurrently where possible
- Diagnostics collected in parallel
- Fix applications batched by file

### Memory Management

- Large solutions processed incrementally
- Workspaces disposed after use
- Results streamed when possible

## Security Architecture

### Input Validation

- Path traversal prevention
- Regex pattern validation
- ID whitelist validation

### Isolation

- No code execution from analyzed projects
- Read-only operations by default
- Temporary workspace isolation

### Output Sanitization

- Error messages sanitized
- Paths normalized
- Sensitive data excluded

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
├── Services/
│   ├── SolutionAnalyzerServiceTests.cs
│   ├── CodeFixServiceTests.cs
│   └── ...
├── Tools/
│   └── AnalysisToolsTests.cs
└── TestUtilities/
    ├── MockFactory.cs
    └── TestData.cs
```

### Test Patterns

- **Arrange-Act-Assert**: Standard test structure
- **Mock Dependencies**: Interface-based mocking
- **Test Data Builders**: Fluent test data creation
- **Integration Tests**: End-to-end tool testing

## Deployment Architecture

### Docker Support (Future)

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY published/ .
ENTRYPOINT ["dotnet", "RoselineMCP.dll"]
```

### CI/CD Pipeline

1. Build → Test → Package → Publish
2. Automated testing on PR
3. Release builds on tags
4. NuGet package publication

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