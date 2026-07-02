# RoselineMCP API Documentation

Complete API reference for RoselineMCP tools and services.

## Table of Contents

- [MCP Tools](#mcp-tools)
  - [AnalyzeSolution](#analyzesolution)
  - [ListDiagnostics](#listdiagnostics)
  - [ApplyFixes](#applyfixes)
  - [CreatePatch](#createpatch)
- [Tool Annotations](#tool-annotations)
- [Service Interfaces](#service-interfaces)
- [Models](#models)
- [Error Handling](#error-handling)

## MCP Tools

All tools are exposed via the Model Context Protocol and return JSON responses.

### AnalyzeSolution

Analyzes an entire C# solution for diagnostics with filtering options. **Read-only** — never
modifies files on disk (`readOnlyHint: true`, see [Tool Annotations](#tool-annotations)).

`pathOrGit` accepts a local `.sln` file, a directory containing one, or an `http(s)://` Git URL.
A Git URL is shallow-cloned (`git clone --depth 1`, optionally with `--branch`) into a temporary
directory that is deleted once analysis finishes; no other URL scheme (`ssh://`, `git://`,
`file://`, ...) is treated as a Git remote.

#### Request

```typescript
{
  pathOrGit: string;       // Path to .sln file, directory with .sln, or an http(s):// Git URL
  branch?: string;         // Git branch (only used if pathOrGit is a Git URL)
  include?: string;        // Only analyze projects whose name contains this substring (case-sensitive; NOT a regex)
  exclude?: string;        // Skip projects whose name contains this substring (case-sensitive; NOT a regex)
  severity?: string;       // Minimum severity: "Error" | "Warning" | "Info" | "Hidden" (case-insensitive)
  maxDiagnostics?: number; // Maximum diagnostics to return (default: 100)
}
```

#### Response

**Returns:** the solution's file name, project count, a diagnostic count summary by severity, and
the top diagnostics (capped at `maxDiagnostics`, ordered by severity then file then line).

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
  "exclude": "Tests",
  "severity": "warning",
  "maxDiagnostics": 50
}'
```

### ListDiagnostics

Gets detailed diagnostics for a specific project with statistics. **Read-only** — never modifies
files on disk.

#### Request

```typescript
{
  project: string;        // Project name or path to .csproj
  ids?: string[];        // Filter by diagnostic IDs (exact match)
  files?: string[];      // Substring match against each diagnostic's file path (case-insensitive; NOT a glob pattern)
  max?: number;          // Maximum diagnostics (default: 100)
}
```

#### Response

**Returns:** the project name, `totalDiagnostics` (the count *before* `max` is applied), the
capped `diagnostics` list, `stats` grouped by ID and by severity, and `suggestedFixableIds` — the
diagnostic IDs for which a code fix provider was actually discovered at runtime (the single source
of truth for "fixable" is `ICodeFixProviderFactory`, not a hand-maintained list).

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
  "files": ["Controllers"],
  "max": 25
}'
```

### ApplyFixes

Applies automated code fixes for specified diagnostic IDs. **Defaults to preview mode**: the
`ApplyFixes` MCP tool defaults `previewOnly` to `true`, so calling it without setting the
parameter never writes to disk — pass `previewOnly: false` explicitly to write changes
(`readOnlyHint: false`, `destructiveHint: true` as a worst-case annotation; see
[Tool Annotations](#tool-annotations)).

#### Request

```typescript
{
  project: string;       // Project name or path to .csproj
  ids: string[];         // Diagnostic IDs to fix (required, at least one)
  previewOnly?: boolean; // If true (the default), only generate a diff — no files written. Pass false to apply.
}
```

> Note: the underlying `ICodeFixService.ApplyFixesAsync` C# method itself defaults `previewOnly`
> to `false` (see [Service Interfaces](#service-interfaces) below) — that default only matters for
> code calling the service directly. Every call through the MCP `ApplyFixes` tool always passes an
> explicit value, and the tool-level default is `true`, per the "Read-Only by Default" guarantee.

#### Response

```typescript
{
  project: string;           // Project name
  fixedCount: number;        // Total number of individual fixes applied across all requested IDs
  fixersApplied: string[];   // Diagnostic IDs that were successfully fixed at least once
  changedFiles: string[];    // Relative paths of files that were modified
  patch: string;             // Unified diff across all changed files
  notes: string[];           // Per-ID status messages: skipped (no provider/no diagnostics), errors, or "applied N fixes to M files" / "Preview mode - no changes were saved to disk"
  previewOnly: boolean;      // Echoes back whether this call actually wrote to disk
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

Generates a unified diff patch between two text versions. **Read-only** — operates purely on the
two provided strings and never touches the filesystem.

#### Request

```typescript
{
  before: string;             // Original text content
  after: string;              // Modified text content
  fileName?: string;          // Optional file name for the diff header (default: "file.txt")
  ignoreWhitespace?: boolean; // Ignore whitespace-only differences (default: false)
  ignoreCase?: boolean;       // Ignore case differences (default: false)
}
```

#### Response

**Returns:** the unified diff, whether anything changed, and added/removed line counts.

```typescript
{
  patch: string;         // Unified diff format patch
  hasChanges: boolean;   // Whether any changes exist
  linesAdded: number;    // Number of lines added
  linesRemoved: number;  // Number of lines removed
  fileName: string;      // File name used in the diff header (default: "file.txt")
  summary: string;       // Human-readable summary, e.g. "file.txt: +2, -1 lines", or "No changes detected"
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

## Tool Annotations

Every tool declares the standard MCP annotation hints (`readOnlyHint`, `destructiveHint`,
`idempotentHint`) via `[McpServerTool(ReadOnly = ..., Destructive = ..., Idempotent = ...)]`:

| Tool | readOnlyHint | destructiveHint | idempotentHint |
|------|:---:|:---:|:---:|
| `AnalyzeSolution` | `true` | `false` | `true` |
| `ListDiagnostics` | `true` | `false` | `true` |
| `ApplyFixes` | `false` | `true`\* | `false` |
| `CreatePatch` | `true` | `false` | `true` |

\* `ApplyFixes`' `destructiveHint` is a static, worst-case annotation: it is `true` because the
tool *can* write files when `previewOnly: false` is passed, even though the default call
(`previewOnly` unset, i.e. `true`) writes nothing. The MCP SDK's annotation model has no way to
express "destructive only for a specific parameter value" — see the doc comment on
`ApplyFixesTool.ApplyFixes` in source for the full rationale.

These hints are per-tool metadata, not a guarantee about any individual call's outcome. See the
README's [Tool Compatibility Policy](../README.md#tool-compatibility-policy) for the stability
guarantees around tool names, parameters, and response shapes that sit underneath them.

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
        List<string> ids,
        bool previewOnly = false); // NOTE: this C# default is `false`; the MCP `ApplyFixes`
                                    // tool always passes an explicit value and defaults to
                                    // `true` at that boundary — see the ApplyFixes tool section above.
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

    CreatePatchResponse CreatePatchWithOptions(
        string before,
        string after,
        string? fileName = null,
        int contextLines = 3,
        bool ignoreWhitespace = false,
        bool ignoreCase = false);
}
```

### IDiagnosticFilterService

```csharp
public interface IDiagnosticFilterService
{
    // Both patterns are plain case-sensitive substring (Contains) matches, not regex/glob.
    bool ShouldAnalyzeProject(string projectName, string? includePattern, string? excludePattern);
    bool ShouldIncludeDiagnostic(Diagnostic diagnostic, string? severityFilter);
    bool FilterByIds(Diagnostic diagnostic, List<string>? ids);
    // Substring (case-insensitive) match against the diagnostic's file path, not a glob.
    bool FilterByFiles(Diagnostic diagnostic, List<string>? files);
    int GetSeverityPriority(string severity);
    // Single source of truth: backed by ICodeFixProviderFactory.GetFixableDiagnosticIds(),
    // i.e. whatever Roslyn/Roslynator code fix providers were actually discovered at runtime.
    bool IsFixableDiagnostic(string id);
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

### ApplyFixesResponse

```csharp
public class ApplyFixesResponse
{
    public string Project { get; set; }
    public List<string> FixersApplied { get; set; }  // JSON: "fixersApplied"
    public List<string> ChangedFiles { get; set; }    // JSON: "changedFiles"
    public string Patch { get; set; }
    public List<string> Notes { get; set; }
    public int FixedCount { get; set; }               // JSON: "fixedCount"
    public bool PreviewOnly { get; set; }              // JSON: "previewOnly"
}
```

### CreatePatchResponse

```csharp
public class CreatePatchResponse
{
    public string Patch { get; set; }
    public bool HasChanges { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string FileName { get; set; } = "file.txt";
    public string Summary { get; set; }
}
```

## Error Handling

Tools never throw to the MCP protocol layer — every failure (validation, not-found, cancellation,
timeout, or unexpected exception) is caught and returned as a JSON object with a stable, closed
set of `type` values. Raw CLR exception type names (`ex.GetType().Name`) are **never** surfaced;
every failure is classified first.

### Error Response Format

```typescript
{
  error: string;   // Human-readable message. For InternalError, this is always the fixed string
                    // "An unexpected internal error occurred. Check the server logs for details."
                    // — the real exception message/stack trace is logged server-side only, never returned.
  type: string;     // One of: "ValidationError" | "NotFoundError" | "AnalysisError" |
                     // "CancelledError" | "TimeoutError" | "InternalError"
  hint?: string;    // Present on some ValidationError responses: suggests the concrete fix
                     // (e.g. the accepted enum values, or which tool to call first)
  correlationId: string;  // Per-invocation GUID, always present. Lets a user reporting a failure
                           // hand you one ID that ties back to the full server-side log entry for
                           // that call (see "Tracing Individual Tool Calls" in the README).
}
```

### Error Types

| `type` | Meaning | Example trigger |
|--------|---------|------------------|
| `ValidationError` | Caller-supplied input was missing, malformed, or otherwise invalid | Unrecognized `severity` string; `ApplyFixes` called with an empty `ids` array |
| `NotFoundError` | The requested solution, project, or file could not be located | `FileNotFoundException`, `DirectoryNotFoundException` |
| `AnalysisError` | Failure while analyzing, building, or fetching the target | MSBuild workspace load failure, Git clone failure/timeout |
| `CancelledError` | The caller's own cancellation token was triggered before completion | Client disconnects/cancels mid-call |
| `TimeoutError` | The call exceeded the configured wall-clock timeout | `RoselineMCP:DefaultTimeout` elapsed (120,000 ms by default; 0 disables it) |
| `InternalError` | Unexpected, unclassified failure | Any exception not mapped to the categories above |

### Error Examples

```json
{
  "error": "Solution file not found: /path/to/missing.sln",
  "type": "NotFoundError",
  "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21"
}
```

```json
{
  "error": "No diagnostic IDs provided.",
  "type": "ValidationError",
  "hint": "Call ListDiagnostics first to discover fixable diagnostic IDs for this project, then pass one or more of them, e.g. ids: [\"RCS1213\"].",
  "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21"
}
```

```json
{
  "error": "Operation timed out after 120000ms",
  "type": "TimeoutError",
  "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21"
}
```

## Performance Characteristics

There is no built-in request rate limiting — callers are responsible for their own throttling if
needed. Each MCP tool call is bounded by a configurable wall-clock timeout instead (see
`RoselineMCP:DefaultTimeout` above and `docs/ARCHITECTURE.md`). Rough complexity per call:

- **AnalyzeSolution**: proportional to the number of projects times diagnostics per project;
  projects within a solution are analyzed sequentially, not concurrently
- **ListDiagnostics**: proportional to diagnostics in the target project
- **ApplyFixes**: proportional to files touched times diagnostics fixed per file; each diagnostic
  ID is fixed occurrence-by-occurrence, re-analyzing the solution after every applied fix
- **CreatePatch**: proportional to the number of lines in the two inputs

### Recommendations

1. **Use filtering**: Apply `include`/`exclude` (substring match) to reduce `AnalyzeSolution` scope
2. **Limit results**: Use `maxDiagnostics`/`max` to cap response size
3. **Preview first**: Leave `ApplyFixes`' `previewOnly` at its default (`true`) to review changes
   before passing `false` to write them
4. **Batch operations**: Group related diagnostic IDs into a single `ApplyFixes` call

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

The package follows Semantic Versioning. Per the
[Tool Compatibility Policy](../README.md#tool-compatibility-policy):
- **Major**: Breaking changes — renamed/removed tool names or parameters, changed
  required/optional status, or removed/renamed response fields
- **Minor**: New tools, or new optional parameters/response fields
- **Patch**: Bug fixes and non-behavioral improvements

See [`CHANGELOG.md`](../CHANGELOG.md) for the release history and current version, and its
"Breaking Changes" headings for anything that shipped as a major bump.