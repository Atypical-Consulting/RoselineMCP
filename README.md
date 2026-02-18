# RoselineMCP

A high-performance MCP (Model Context Protocol) server that provides comprehensive code analysis and automated fixing capabilities for C# solutions using Roslyn analyzers and code fix providers.

## Features

- **Comprehensive Code Analysis**: Analyze entire C# solutions for code quality issues, potential bugs, and style violations
- **Automated Code Fixes**: Apply automated fixes for hundreds of diagnostic rules from Roslyn and Roslynator
- **Unified Diff Generation**: Generate reviewable patches before applying changes
- **Flexible Filtering**: Filter diagnostics by severity, ID, file patterns, and project names
- **Safe Operations**: All operations use temporary workspaces to prevent accidental modifications
- **MCP Protocol Support**: Full integration with the Model Context Protocol for AI assistant usage

## Installation

Choose the installation method that best fits your workflow:

### Option 1 — NuGet Global Tool (recommended)

Requires .NET 9.0 SDK or later.

```bash
dotnet tool install -g RoselineMCP
```

After installation, the `roseline-mcp` command is available globally.

#### Claude Desktop configuration (NuGet global tool)

Add to your Claude Desktop configuration file (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "roseline": {
      "command": "roseline-mcp"
    }
  }
}
```

> **Config file location:**
> - macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
> - Windows: `%APPDATA%\Claude\claude_desktop_config.json`

---

### Option 2 — Docker

No SDK required. Works on any platform with Docker installed.

```bash
docker run -i --rm phmatray/roseline-mcp:latest
```

#### Claude Desktop configuration (Docker)

```json
{
  "mcpServers": {
    "roseline": {
      "command": "docker",
      "args": [
        "run",
        "-i",
        "--rm",
        "phmatray/roseline-mcp:latest"
      ]
    }
  }
}
```

> **Note:** The `-i` flag is required for stdio transport. The `--rm` flag removes the container after the session ends.

---

### Option 3 — Build from Source

```bash
# Clone the repository
git clone https://github.com/Atypical-Consulting/RoselineMCP.git
cd RoselineMCP

# Build the project
dotnet build

# Run tests
dotnet test
```

#### Claude Desktop configuration (build from source)

```json
{
  "mcpServers": {
    "roseline": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/RoselineMCP/RoselineMCP.csproj"]
    }
  }
}
```

---

### Prerequisites

- **NuGet global tool**: .NET 9.0 SDK or later
- **Docker**: Docker Desktop or Docker Engine
- **Build from source**: .NET 9.0 SDK + MSBuild (included with Visual Studio or .NET SDK)
- **MCP client**: Claude Desktop or any MCP-compatible client

## Available Tools

### 1. AnalyzeSolution

Analyzes an entire C# solution for diagnostics.

```typescript
analyzeSolution({
  pathOrGit: "/path/to/solution.sln",
  includePattern: "*.Core",     // Optional: Include only matching projects
  excludePattern: "*.Tests",    // Optional: Exclude matching projects
  severity: "warning",           // Optional: Minimum severity (error|warning|info)
  maxDiagnostics: 100           // Optional: Maximum diagnostics to return
})
```

### 2. ListDiagnostics

Gets detailed diagnostics for a specific project.

```typescript
listDiagnostics({
  project: "MyProject.csproj",
  ids: ["CS0168", "CS0219"],    // Optional: Filter by diagnostic IDs
  files: ["**/Controllers/*"],   // Optional: Filter by file patterns
  max: 50                        // Optional: Maximum results
})
```

### 3. ApplyFixes

Applies automated code fixes for specified diagnostics.

```typescript
applyFixes({
  project: "MyProject.csproj",
  ids: ["CS0168", "RCS1001"],   // Diagnostic IDs to fix
  previewOnly: true              // Optional: Generate patch without applying
})
```

### 4. CreatePatch

Generates a unified diff between two text versions.

```typescript
createPatch({
  before: "original code",
  after: "modified code",
  fileName: "Example.cs"         // Optional: For display in diff
})
```

## Supported Analyzers

RoselineMCP includes support for:

- **Roslyn Analyzers**: Built-in C# compiler diagnostics
- **Roslynator**: 500+ analyzers, refactorings, and fixes for C#
- **StyleCop Analyzers**: Code style and consistency rules
- **Custom Analyzers**: Any Roslyn-based analyzer in your solution

## Examples

### Analyzing a Solution

```bash
# Using with an MCP client
mcp call analyzeSolution '{
  "pathOrGit": "/Users/dev/MyProject/MyProject.sln",
  "severity": "warning",
  "maxDiagnostics": 50
}'
```

Response:
```json
{
  "solution": "MyProject.sln",
  "projects": 5,
  "diagnosticSummary": {
    "error": 2,
    "warning": 15,
    "info": 28
  },
  "topDiagnostics": [
    {
      "id": "CS0168",
      "severity": "warning",
      "message": "The variable 'ex' is declared but never used",
      "file": "Program.cs",
      "line": 42
    }
  ]
}
```

### Applying Fixes

```bash
mcp call applyFixes '{
  "project": "MyProject.Core.csproj",
  "ids": ["CS0168", "RCS1001"],
  "previewOnly": true
}'
```

Response includes a unified diff patch showing all changes that would be applied.

## Configuration

### Environment Variables

- `ROSELINE_LOG_LEVEL`: Set logging level (Debug, Information, Warning, Error)
- `ASPNETCORE_ENVIRONMENT`: Set environment (Development, Production)

### appsettings.json

Configure logging and other settings:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "RoselineMCP": "Debug"
    }
  }
}
```

## Development

### Project Structure

```
RoselineMCP/
├── RoselineMCP/
│   ├── Interfaces/       # Service interfaces
│   ├── Services/         # Core business logic
│   ├── Tools/           # MCP tool implementations
│   ├── Models/          # Data transfer objects
│   └── Program.cs       # Application entry point
└── RoselineMCP.Tests/   # Unit tests
```

### Adding New Tools

1. Create a new tool class in `Tools/`
2. Add the `[McpServerTool]` attribute
3. Implement the tool logic
4. Add corresponding tests

See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines.

## Performance

- **Workspace Caching**: MSBuild workspaces are reused when possible
- **Parallel Analysis**: Projects analyzed concurrently
- **Streaming Results**: Large result sets streamed to prevent memory issues
- **Lazy Loading**: Diagnostics computed on-demand

## Security

- **No Code Execution**: Never executes code from analyzed projects
- **Sandboxed Operations**: All changes made in temporary workspaces
- **Path Validation**: Protection against path traversal attacks
- **Read-Only by Default**: Explicit confirmation required for modifications

## Troubleshooting

### Common Issues

1. **MSBuild not found**: Ensure .NET SDK is installed and in PATH
2. **Solution won't load**: Check for missing NuGet packages, run `dotnet restore`
3. **No diagnostics found**: Verify analyzers are installed in the target project
4. **Permission denied**: Ensure read access to solution files

### Debug Logging

Enable detailed logging:

```bash
ROSELINE_LOG_LEVEL=Debug dotnet run --project RoselineMCP/RoselineMCP.csproj
```

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Support

- **Issues**: [GitHub Issues](https://github.com/phmatray/RoselineMCP/issues)
- **Discussions**: [GitHub Discussions](https://github.com/phmatray/RoselineMCP/discussions)

## Acknowledgments

- Built on [Roslyn](https://github.com/dotnet/roslyn) - The .NET Compiler Platform
- Powered by [Roslynator](https://github.com/JosefPihrt/Roslynator) - C# analyzers and refactorings
- Uses [DiffPlex](https://github.com/mmanela/diffplex) - Diff generation library
- Implements [Model Context Protocol](https://modelcontextprotocol.io) - AI assistant integration protocol