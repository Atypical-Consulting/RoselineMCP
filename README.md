# RoselineMCP

> **Expose Roslyn code analysis as an MCP server so AI assistants can analyze and fix C# quality issues directly.**

<!-- Badges: Row 1 — Identity -->
[![Atypical-Consulting - RoselineMCP](https://img.shields.io/static/v1?label=Atypical-Consulting&message=RoselineMCP&color=blue&logo=github)](https://github.com/Atypical-Consulting/RoselineMCP)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple?logo=dotnet)](https://dotnet.microsoft.com/)
[![stars - RoselineMCP](https://img.shields.io/github/stars/Atypical-Consulting/RoselineMCP?style=social)](https://github.com/Atypical-Consulting/RoselineMCP)
[![forks - RoselineMCP](https://img.shields.io/github/forks/Atypical-Consulting/RoselineMCP?style=social)](https://github.com/Atypical-Consulting/RoselineMCP)

<!-- Badges: Row 2 — Activity -->
[![GitHub tag](https://img.shields.io/github/tag/Atypical-Consulting/RoselineMCP?include_prereleases=&sort=semver&color=blue)](https://github.com/Atypical-Consulting/RoselineMCP/releases/)
[![issues - RoselineMCP](https://img.shields.io/github/issues/Atypical-Consulting/RoselineMCP)](https://github.com/Atypical-Consulting/RoselineMCP/issues)
[![GitHub pull requests](https://img.shields.io/github/issues-pr/Atypical-Consulting/RoselineMCP)](https://github.com/Atypical-Consulting/RoselineMCP/pulls)
[![GitHub last commit](https://img.shields.io/github/last-commit/Atypical-Consulting/RoselineMCP)](https://github.com/Atypical-Consulting/RoselineMCP/commits/main)

<!-- Badges: Row 3 — Quality -->
[![CI](https://github.com/Atypical-Consulting/RoselineMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/Atypical-Consulting/RoselineMCP/actions/workflows/ci.yml)

<!-- Badges: Row 4 — Distribution -->
[![NuGet](https://img.shields.io/nuget/v/RoselineMCP.svg)](https://www.nuget.org/packages/RoselineMCP/)
[![Docker](https://img.shields.io/docker/v/phmatray/roseline-mcp?label=docker)](https://hub.docker.com/r/phmatray/roseline-mcp)

---

## Table of Contents

- [The Problem](#the-problem)
- [The Solution](#the-solution)
- [Features](#features)
- [Tech Stack](#tech-stack)
- [Getting Started](#getting-started)
- [Available Tools](#available-tools)
- [Architecture](#architecture)
- [Project Structure](#project-structure)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)
- [Acknowledgments](#acknowledgments)

## The Problem

C# codebases accumulate quality issues silently -- inconsistent naming, missing nullable annotations, unused usings, suboptimal patterns. Manual code reviews catch some, but Roslyn analyzers can catch them all automatically. The problem: setting up and running analyzers across large codebases is tedious, and the results are not easily actionable from an AI assistant workflow.

## The Solution

**RoselineMCP** exposes Roslyn analysis as an MCP server so AI assistants can analyze and fix C# code quality issues directly. Connect it to Claude Desktop or any MCP-compatible client and get comprehensive diagnostics, automated fixes, and reviewable patches -- all without leaving your AI workflow.

```csharp
// In your Claude Desktop config, just add:
{
  "mcpServers": {
    "roseline": {
      "command": "roseline-mcp"
    }
  }
}
// Then ask Claude: "Analyze my solution at /path/to/MySolution.sln"
```

## Features

- [x] **Comprehensive Code Analysis** -- Analyze entire C# solutions for code quality issues, potential bugs, and style violations
- [x] **Automated Code Fixes** -- Apply automated fixes for hundreds of diagnostic rules from Roslyn and Roslynator
- [x] **Unified Diff Generation** -- Generate reviewable patches before applying changes
- [x] **Flexible Filtering** -- Filter diagnostics by severity, ID, file patterns, and project names
- [x] **Safe Operations** -- All operations use temporary workspaces to prevent accidental modifications
- [x] **MCP Protocol Support** -- Full integration with the Model Context Protocol for AI assistant usage
- [x] **Docker Support** -- Run without any SDK installation via Docker
- [x] **NuGet Global Tool** -- Install with a single `dotnet tool install` command

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 10.0 |
| Compiler Platform | Roslyn (Microsoft.CodeAnalysis) 5.3.0 |
| Analyzers | Roslynator 4.15.0 |
| MCP SDK | ModelContextProtocol 1.1.0 |
| Diff Engine | DiffPlex 1.9.0 |
| Build System | MSBuild 18.4.0 |
| Hosting | Microsoft.Extensions.Hosting 10.0.5 |

## Getting Started

### Prerequisites

- **NuGet global tool**: .NET 10.0 SDK or later
- **Docker**: Docker Desktop or Docker Engine
- **Build from source**: .NET 10.0 SDK + MSBuild (included with Visual Studio or .NET SDK)
- **MCP client**: Claude Desktop or any MCP-compatible client

### Installation

**Option 1 -- NuGet Global Tool** *(recommended)*

Requires .NET 10.0 SDK or later.

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

**Option 2 -- Docker**

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

**Option 3 -- Build from Source**

```bash
git clone https://github.com/Atypical-Consulting/RoselineMCP.git
cd RoselineMCP
dotnet build
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

- **Roslyn Analyzers** -- Built-in C# compiler diagnostics
- **Roslynator** -- 500+ analyzers, refactorings, and fixes for C#
- **StyleCop Analyzers** -- Code style and consistency rules
- **Custom Analyzers** -- Any Roslyn-based analyzer in your solution

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

## Architecture

RoselineMCP uses **stdio transport** — this is an intentional design decision. The server runs as a local process launched by the MCP client (Claude Desktop, AI agents), communicates over stdin/stdout, and exits when the client disconnects. This makes it perfectly suited for distribution as a NuGet global tool (`dotnet tool install -g RoselineMCP`) or Docker image — no port binding, no HTTP server, no infrastructure to manage.

```
┌─────────────────────┐
│   MCP Client        │
│  (Claude Desktop,   │
│   AI Assistants)    │
└────────┬────────────┘
         │ MCP Protocol (stdio)
         ▼
┌─────────────────────┐
│   RoselineMCP       │
│   MCP Server        │
│                     │
│  ┌───────────────┐  │
│  │  Tool Layer   │  │
│  │  (Analyze,    │  │
│  │   Fix, Patch) │  │
│  └───────┬───────┘  │
│          ▼          │
│  ┌───────────────┐  │
│  │ Service Layer │  │
│  │ (Workspace,   │  │
│  │  Diagnostics) │  │
│  └───────┬───────┘  │
│          ▼          │
│  ┌───────────────┐  │
│  │ Roslyn +      │  │
│  │ Roslynator    │  │
│  │ Analyzers     │  │
│  └───────────────┘  │
└─────────────────────┘
         │
         ▼
┌─────────────────────┐
│  C# Source Code     │
│  (.sln / .csproj)   │
└─────────────────────┘
```

## Project Structure

```
RoselineMCP/
├── RoselineMCP/
│   ├── Interfaces/       # Service interfaces
│   ├── Services/         # Core business logic
│   ├── Tools/            # MCP tool implementations
│   ├── Models/           # Data transfer objects
│   └── Program.cs        # Application entry point
├── RoselineMCP.Tests/    # Unit tests
├── .github/workflows/    # CI/CD pipelines
├── Dockerfile            # Container build
└── RoselineMCP.sln       # Solution file
```

## Performance

- **Workspace Caching** -- MSBuild workspaces are reused when possible
- **Parallel Analysis** -- Projects analyzed concurrently
- **Streaming Results** -- Large result sets streamed to prevent memory issues
- **Lazy Loading** -- Diagnostics computed on-demand

## Security

- **No Code Execution** -- Never executes code from analyzed projects
- **Sandboxed Operations** -- All changes made in temporary workspaces
- **Path Validation** -- Protection against path traversal attacks
- **Read-Only by Default** -- Explicit confirmation required for modifications

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

## Roadmap

- [ ] Additional analyzer rule sets (SonarAnalyzer, FxCop)
- [ ] Auto-fix suggestions with confidence scoring
- [ ] CI/CD integration for automated analysis pipelines
- [ ] Multi-solution support in a single session
- [ ] Incremental analysis (only changed files)
- [ ] Custom analyzer rule configuration via MCP

> Want to contribute? Pick any roadmap item and open a PR!

## Stats

## Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) first.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit using [conventional commits](https://www.conventionalcommits.org/) (`git commit -m 'feat: add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

[MIT](LICENSE) © 2025 [Atypical Consulting](https://atypical.garry-ai.cloud)

## Acknowledgments

- Built on [Roslyn](https://github.com/dotnet/roslyn) -- The .NET Compiler Platform
- Powered by [Roslynator](https://github.com/JosefPihrt/Roslynator) -- C# analyzers and refactorings
- Uses [DiffPlex](https://github.com/mmanela/diffplex) -- Diff generation library
- Implements [Model Context Protocol](https://modelcontextprotocol.io) -- AI assistant integration protocol

---

Built with care by [Atypical Consulting](https://atypical.garry-ai.cloud) -- opinionated, production-grade open source.

[![Contributors](https://contrib.rocks/image?repo=Atypical-Consulting/RoselineMCP)](https://github.com/Atypical-Consulting/RoselineMCP/graphs/contributors)
