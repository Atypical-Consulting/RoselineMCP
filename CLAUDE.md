# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a .NET 9.0 MCP (Model Context Protocol) server implementation that provides tools for interacting with monkey data from an external API.

## Architecture

The solution follows the MCP Server pattern:
- **Program.cs**: Configures and hosts the MCP server using stdio transport
- **MonkeyService.cs**: HTTP service that fetches and caches monkey data from https://www.montemagno.com/monkeys.json
- **MonkeyTools.cs**: MCP tool definitions for GetMonkeys and GetMonkey operations
- **EchoTool.cs**: Simple echo tools for testing MCP communication

Key architectural decisions:
- Uses `ModelContextProtocol.Server` NuGet package for MCP implementation
- Tools are auto-discovered via `WithToolsFromAssembly()` 
- HTTP client is injected via IHttpClientFactory for proper lifecycle management
- MonkeyService implements in-memory caching to avoid repeated API calls

## Common Commands

### Build
```bash
dotnet build
```

### Run
```bash
dotnet run --project MyFirstMCP/MyFirstMCP.csproj
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

## Dependencies

- **ModelContextProtocol** (0.3.0-preview.4): Core MCP server implementation
- **Microsoft.Extensions.Hosting**: Application hosting and DI
- **Microsoft.Extensions.Http**: HTTP client factory