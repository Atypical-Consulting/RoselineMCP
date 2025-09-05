# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**RoselineMCP** is a .NET 9.0 MCP (Model Context Protocol) server implementation that provides comprehensive code analysis and automated fixing tools for C# solutions.

## Architecture

The solution follows the MCP Server pattern:
- **Program.cs**: Configures and hosts the MCP server using stdio transport
- **Services/SolutionAnalyzerService.cs**: Analyzes C# solutions and projects for diagnostics
- **Services/CodeFixService.cs**: Applies Roslyn code fixes to resolve diagnostics
- **Services/PatchService.cs**: Generates unified diff patches between text versions
- **Tools/AnalysisTools.cs**: MCP tool definitions for all analysis operations

Key architectural decisions:
- Uses `ModelContextProtocol.Server` NuGet package for MCP implementation
- Tools are auto-discovered via `WithToolsFromAssembly()` 
- Leverages Roslyn for code analysis and fixes
- Uses DiffPlex for unified diff generation
- Implements temporary workspace isolation for safe operations

## Common Commands

### Build
```bash
dotnet build
```

### Run
```bash
dotnet run --project RoselineMCP/RoselineMCP.csproj
```

### Clean build
```bash
dotnet clean && dotnet build
```

### Restore packages
```bash
dotnet restore
```

## MCP Tool Development

When adding new MCP tools:
1. Mark the class with `[McpServerToolType]` attribute
2. Mark tool methods with `[McpServerTool]` and `[Description("...")]` attributes
3. Use `[Description("...")]` on parameters to document their purpose
4. Tools are auto-discovered at startup - no registration needed

## MCP Tools Available

1. **AnalyzeSolution**: Analyze C# solutions for diagnostics with filtering options
2. **ListDiagnostics**: Get detailed diagnostics for specific projects
3. **ApplyFixes**: Apply automated code fixes for specified diagnostic IDs
4. **CreatePatch**: Generate unified diff patches between text versions

## Dependencies

- **ModelContextProtocol** (0.3.0-preview.4): Core MCP server implementation
- **Microsoft.Extensions.Hosting**: Application hosting and DI
- **Microsoft.CodeAnalysis**: Roslyn for code analysis and fixes
- **Microsoft.Build.Locator**: MSBuild location for solution loading
- **Roslynator**: Additional analyzers and code fixes
- **DiffPlex**: Unified diff generation