# RoselineMCP API Documentation

Complete API reference for RoselineMCP tools and services.

## Table of Contents

- [MCP Tools](#mcp-tools)
  - [AnalyzeSolution](#analyzesolution)
  - [ListDiagnostics](#listdiagnostics)
  - [ApplyFixes](#applyfixes)
  - [CreatePatch](#createpatch)
- [Service Interfaces](#service-interfaces)
- [Models](#models)
- [Error Handling](#error-handling)

## MCP Tools

All tools are exposed via the Model Context Protocol and return JSON responses.

### AnalyzeSolution

Analyzes an entire C# solution for diagnostics with filtering options.

#### Request

```typescript
{
  pathOrGit: string;      // Path to .sln file, directory with .sln, or Git URL
  branch?: string;        // Git branch (only for Git URLs) 
  include?: string;       // Regex pattern to include projects
  exclude?: string;       // Regex pattern to exclude projects
  severity?: string;      // Minimum severity: "error" | "warning" | "info" | "hidden"
  maxDiagnostics?: number; // Maximum diagnostics to return (default: 100)
}
```

#### Response

```typescript
{
  solution: string;              // Solution file name
  projects: number;              // Number of projects analyzed
  diagnosticSummary: {
    error: number;
    warning: number;
    info: number;
    hidden: number;
  };
  topDiagnostics: Array<{
    project: string;             // Project name
    file: string;               // File path
    line: number;               // Line number (1-based)
    column: number;             // Column number (1-based)
    id: string;                 // Diagnostic ID (e.g., "CS0168")
    severity: string;           // "error" | "warning" | "info" | "hidden"
    message: string;            // Diagnostic message
  }>;
}
```

#### Example

```bash
mcp call analyzeSolution '{
  "pathOrGit": "/path/to/MySolution.sln",
  "exclude": ".*\\.Tests",
  "severity": "warning",
  "maxDiagnostics": 50
}'
```

### ListDiagnostics

Gets detailed diagnostics for a specific project with statistics.

#### Request

```typescript
{
  project: string;        // Project name or path to .csproj
  ids?: string[];        // Filter by diagnostic IDs
  files?: string[];      // Filter by file patterns (glob)
  max?: number;          // Maximum diagnostics (default: 100)
}
```

#### Response

```typescript
{
  project: string;                    // Project name
  totalDiagnostics: number;          // Total count before limiting
  diagnostics: Array<{
    project: string;
    file: string;
    line: number;
    column: number;
    id: string;
    severity: string;
    message: string;
  }>;
  stats: {
    byId: Record<string, number>;     // Count by diagnostic ID
    bySeverity: Record<string, number>; // Count by severity
  };
  suggestedFixableIds: string[];      // IDs with available fixes
}
```

#### Example

```bash
mcp call listDiagnostics '{
  "project": "MyApp.Core.csproj",
  "ids": ["CS0168", "CS0219"],
  "files": ["**/Controllers/**/*.cs"],
  "max": 25
}'
```

### ApplyFixes

Applies automated code fixes for specified diagnostic IDs.

#### Request

```typescript
{
  project: string;       // Project name or path to .csproj
  ids: string[];        // Diagnostic IDs to fix
  previewOnly?: boolean; // Generate patch without applying (default: false)
}
```

#### Response

```typescript
{
  project: string;                   // Project name
  fixesApplied: number;             // Number of fixes applied
  filesChanged: string[];           // List of modified files
  patch: string;                    // Unified diff patch
  appliedFixers: Array<{
    diagnosticId: string;           // Diagnostic ID
    fixerName: string;             // Code fix provider name
    count: number;                 // Number of applications
  }>;
  errors?: Array<{                 // Any errors encountered
    file: string;
    message: string;
  }>;
}
```

#### Example

```bash
mcp call applyFixes '{
  "project": "MyApp.Core.csproj",
  "ids": ["CS0168", "RCS1001", "IDE0059"],
  "previewOnly": true
}'
```

### CreatePatch

Generates a unified diff patch between two text versions.

#### Request

```typescript
{
  before: string;        // Original text content
  after: string;         // Modified text content
  fileName?: string;     // Optional file name for display
}
```

#### Response

```typescript
{
  patch: string;         // Unified diff format patch
  linesAdded: number;    // Number of lines added
  linesDeleted: number;  // Number of lines removed
  hasChanges: boolean;   // Whether any changes exist
}
```

#### Example

```bash
mcp call createPatch '{
  "before": "public class Foo {\n  int x;\n}",
  "after": "public class Foo\n{\n    private int x;\n}",
  "fileName": "Foo.cs"
}'
```

## Service Interfaces

### ISolutionAnalyzerService

```csharp
public interface ISolutionAnalyzerService
{
    Task<AnalyzeSolutionResponse> AnalyzeSolutionAsync(
        string pathOrGit,
        string? branch = null,
        string? includePattern = null,
        string? excludePattern = null,
        string? severity = null,
        int maxDiagnostics = 100);

    Task<ListDiagnosticsResponse> ListDiagnosticsAsync(
        string project,
        List<string>? ids = null,
        List<string>? files = null,
        int max = 100);
}
```

### ICodeFixService

```csharp
public interface ICodeFixService
{
    Task<ApplyFixesResponse> ApplyFixesAsync(
        string project,
        List<string> diagnosticIds,
        bool previewOnly = false);
}
```

### IPatchService

```csharp
public interface IPatchService
{
    CreatePatchResponse CreatePatch(
        string before,
        string after,
        string? fileName = null);
}
```

### IDiagnosticFilterService

```csharp
public interface IDiagnosticFilterService
{
    bool ShouldAnalyzeProject(string projectName, string? include, string? exclude);
    bool ShouldIncludeDiagnostic(Diagnostic diagnostic, string? severityFilter);
    bool FilterByIds(Diagnostic diagnostic, List<string>? ids);
    bool FilterByFiles(Diagnostic diagnostic, List<string>? files);
    bool IsFixableDiagnostic(string diagnosticId);
    int GetSeverityPriority(string severity);
}
```

## Models

### DiagnosticDetail

```csharp
public class DiagnosticDetail
{
    public string Project { get; set; }
    public string File { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Id { get; set; }
    public string Severity { get; set; }
    public string Message { get; set; }
}
```

### DiagnosticSummary

```csharp
public class DiagnosticSummary
{
    public int Error { get; set; }
    public int Warning { get; set; }
    public int Info { get; set; }
    public int Hidden { get; set; }
}
```

### DiagnosticStats

```csharp
public class DiagnosticStats
{
    public Dictionary<string, int> ById { get; set; }
    public Dictionary<string, int> BySeverity { get; set; }
}
```

## Error Handling

All tools return consistent error responses when exceptions occur:

### Error Response Format

```typescript
{
  error: string;        // Error message
  type: string;        // Exception type name
  details?: any;       // Additional error details (optional)
}
```

### Common Error Types

| Error Type | Description | Common Causes |
|------------|-------------|---------------|
| `FileNotFoundException` | Solution or project file not found | Invalid path, missing file |
| `InvalidOperationException` | Operation cannot be performed | Project not loaded, invalid state |
| `NotImplementedException` | Feature not yet implemented | Git URL support pending |
| `ArgumentException` | Invalid argument provided | Malformed regex, invalid ID |
| `UnauthorizedAccessException` | Access denied | File permissions, locked files |

### Error Examples

```json
{
  "error": "Solution file not found: /path/to/missing.sln",
  "type": "FileNotFoundException"
}
```

```json
{
  "error": "Project not found: NonExistent.csproj",
  "type": "InvalidOperationException"
}
```

## Rate Limiting and Performance

### Performance Characteristics

- **AnalyzeSolution**: O(n*m) where n=projects, m=files per project
- **ListDiagnostics**: O(m) where m=files in project
- **ApplyFixes**: O(f*d) where f=files, d=diagnostics per file
- **CreatePatch**: O(n) where n=total lines

### Recommendations

1. **Use filtering**: Apply include/exclude patterns to reduce scope
2. **Limit results**: Use `max` parameter to cap response size
3. **Preview first**: Use `previewOnly` for ApplyFixes to review changes
4. **Batch operations**: Group related diagnostics IDs in single calls

## Supported Diagnostic IDs

### Roslyn (CS/BC)
- CS0168: Variable declared but never used
- CS0219: Variable assigned but never used
- CS0649: Field never assigned
- CS1591: Missing XML documentation
- And 1000+ more...

### Roslynator (RCS)
- RCS1001: Add braces
- RCS1018: Add accessibility modifiers
- RCS1036: Remove redundant empty line
- RCS1097: Remove redundant 'ToString' call
- And 500+ more...

### IDE (IDE)
- IDE0001: Simplify name
- IDE0059: Remove unnecessary value assignment
- IDE0060: Remove unused parameter
- And 100+ more...

## Versioning

The API follows Semantic Versioning:
- **Major**: Breaking changes to request/response format
- **Minor**: New tools or optional parameters
- **Patch**: Bug fixes and performance improvements

Current version: 1.0.0