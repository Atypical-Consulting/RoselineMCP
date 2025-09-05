# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RoselineMCP** is a .NET 9.0 MCP (Model Context Protocol) server that provides comprehensive code analysis and automated fixing capabilities for C# solutions using Roslyn analyzers and code fix providers.

## Architecture

### Core Components

- **Program.cs**: Entry point that configures the MCP server with stdio transport and auto-discovers tools
- **Tools/AnalysisTools.cs**: MCP tool definitions marked with `[McpServerTool]` attributes
- **Services/SolutionAnalyzerService.cs**: Analyzes C# solutions for diagnostics using Roslyn
- **Services/CodeFixService.cs**: Applies automated code fixes for specified diagnostic IDs
- **Services/PatchService.cs**: Generates unified diff patches using DiffPlex
- **Models/AnalysisModels.cs**: Data models for analysis results and fix responses

### Key Design Decisions

- Uses temporary workspace isolation to ensure safe, non-destructive operations
- All fixes are deterministic and reviewable through unified diff patches
- Tools auto-discovered via `WithToolsFromAssembly()` - no manual registration needed
- MSBuildLocator ensures correct solution loading across different environments
- Stderr logging to avoid interfering with MCP stdio communication

## Common Commands

### Build and Run
```bash
# Build the project
dotnet build

# Run the MCP server
dotnet run --project RoselineMCP/RoselineMCP.csproj

# Clean and rebuild
dotnet clean && dotnet build

# Restore packages
dotnet restore
```

### Development Workflow
```bash
# Watch for changes and rebuild
dotnet watch build --project RoselineMCP/RoselineMCP.csproj

# Run with verbose logging
dotnet run --project RoselineMCP/RoselineMCP.csproj --verbosity detailed
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
1. Add method to `AnalysisTools.cs` with `[McpServerTool]` attribute
2. Use `[Description("...")]` on method and all parameters for documentation
3. Accept required services as first parameters (injected automatically)
4. Return JSON-serialized results or error responses
5. Handle exceptions and return structured error JSON

Example:
```csharp
[McpServerTool]
[Description("Tool description")]
public static async Task<string> NewTool(
    RequiredService service,
    [Description("Parameter description")] string param)
{
    // Implementation
}
```

## Error Handling

All tools follow consistent error handling:
- Wrap operations in try-catch blocks
- Return JSON with `error` and `type` fields on failure
- Use appropriate exception types for different failure modes
- Log errors to stderr for debugging (won't interfere with stdio)

## Testing MCP Tools

To test tools locally without a client:
1. Build the project
2. Use a MCP client library or testing tool
3. Connect via stdio transport
4. Call tools with test parameters
5. Verify JSON responses

## Dependencies

### Core MCP and Hosting
- **ModelContextProtocol** (0.3.0-preview.4): MCP server implementation
- **Microsoft.Extensions.Hosting**: Application hosting and DI

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

## Security Considerations

- Never executes arbitrary code from analyzed projects
- Uses read-only Git clones when analyzing remote repositories
- Operates in temporary workspaces to prevent accidental modifications
- All changes are returned as patches for review before application
- Path traversal protection in file operations