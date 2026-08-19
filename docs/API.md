# RoselineMCP API Documentation

Complete API reference for RoselineMCP tools and services.

## Table of Contents

- [MCP Tools](#mcp-tools)
  - [Write Confirmation](#write-confirmation)
  - [AnalyzeSolution](#analyzesolution)
  - [ListDiagnostics](#listdiagnostics)
  - [ApplyFixes](#applyfixes)
  - [CreatePatch](#createpatch)
  - [SearchSymbols](#searchsymbols)
  - [GetSymbolInfo](#getsymbolinfo)
  - [FindReferences](#findreferences)
  - [FindImplementations](#findimplementations)
  - [GetCallGraph](#getcallgraph)
  - [GetTypeHierarchy](#gettypehierarchy)
  - [GetSymbolAtPosition](#getsymbolatposition)
  - [EditMember](#editmember)
  - [RenameSymbol](#renamesymbol)
- [Tool Annotations](#tool-annotations)
- [Service Interfaces](#service-interfaces)
- [Models](#models)
- [Error Handling](#error-handling)

## MCP Tools

All tools are exposed via the Model Context Protocol and return JSON responses.

### Response Envelope

**Every** tool returns the same typed envelope, `ToolResult<T>`, with the shape `{ ok, data, error }`:

- **Success** — `ok` is `true`, the tool's payload is nested under `data`, and `error` is absent:

  ```json
  { "ok": true, "data": { /* ...the tool's payload... */ } }
  ```

- **Failure** — `ok` is `false`, `data` is absent, and the details live under `error`
  (see [Error Handling](#error-handling)):

  ```json
  { "ok": false, "error": { "type": "...", "message": "...", "correlationId": "..." } }
  ```

Each tool's **Response** schema shown below describes the shape of the `data` object (the success
payload), not the envelope. The tools set `UseStructuredContent = true`, so the same object is also
delivered as MCP `structuredContent` alongside an advertised `outputSchema`.

### Write Confirmation

Applies to the three write-capable tools — [`ApplyFixes`](#applyfixes),
[`EditMember`](#editmember) and [`RenameSymbol`](#renamesymbol). All three default to
`previewOnly: true` and write nothing unless the caller passes `previewOnly: false`.

Behind that opt-in sits a second, **best-effort** guard: when `previewOnly: false` is passed, the
server sends an MCP `elicitation/create` asking the connected client to confirm before writing.

| Situation | Result |
|---|---|
| Client accepts | The write proceeds; `previewOnly` comes back `false`. |
| Client **declines** | The call is downgraded to a preview — nothing is written, `previewOnly` comes back `true`, and `notes[]` gains `"Write declined via client confirmation; returned a preview only (no files were modified)."` |
| Client is asked and **never answers** | After `RoselineMCP:ConfirmDestructiveWritesTimeout` (default `300000`, 5 minutes) the server stops waiting and downgrades the call to a preview — nothing is written, `previewOnly` comes back `true`, and `notes[]` gains `"Write confirmation timed out; returned a preview only (no files were modified). Set RoselineMCP:ConfirmDestructiveWrites=false on unattended hosts that should write without a human, or raise RoselineMCP:ConfirmDestructiveWritesTimeout."` |
| Client does not support elicitation, or the round-trip fails | No confirmation is possible, so the explicit opt-in stands and the write proceeds. |
| `RoselineMCP:ConfirmDestructiveWrites` is `false` | **No elicitation is sent at all** (as opposed to one being auto-accepted); the write proceeds. |

Silence is deliberately *not* consent: a client that **cannot** be asked justifies honoring the
explicit opt-in, but one that was asked and said nothing does not. The timeout therefore removes the
hang without weakening the guard — the only way to write without a human remains an explicit
operator decision. Its clock is deliberately **not** `RoselineMCP:DefaultTimeout`: that is an
analysis budget, and a human reading a real diff may legitimately exceed it. Set the timeout to `0`
or less to remove the bound entirely and wait indefinitely (the pre-2.2 behavior).

The last row is an operator switch, not a tool parameter — the model cannot waive the gate. It
defaults to `true`; set it to `false` (via `appsettings.json` or
`ROSELINE_RoselineMCP__ConfirmDestructiveWrites=false`) for unattended hosts such as CI or headless
agents, whose client *can* elicit but has no human to answer, so the prompt would otherwise stall
the call until the timeout above expires and then return a preview rather than the requested write.
The server logs a warning at startup when the switch is off. Disabling it leaves
`previewOnly: false` as the only guard before a write — see
[SECURITY.md](../SECURITY.md).

The response shape is identical whether the confirmation was accepted or skipped; only the decline
and timeout paths add a note.

### AnalyzeSolution

Analyzes an entire C# solution for diagnostics with filtering options. **Read-only** — never
modifies files on disk (`readOnlyHint: true`, see [Tool Annotations](#tool-annotations)).

Diagnostics are compiler diagnostics **plus analyzer diagnostics**: the bundled Roslynator
analyzers and any analyzers the target project itself references are executed via
`CompilationWithAnalyzers` (deduplicated by analyzer type), so RCS*/custom-analyzer diagnostics
surface alongside CS* ones. Set `RoselineMCP:RunAnalyzers` to `false` for compiler-only
diagnostics (faster; the pre-analyzer behavior). This applies equally to `ListDiagnostics` and to
the diagnostics `ApplyFixes` sees.

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
`diagnosticSummary` counts **every** diagnostic that passes the filters across all projects — it is
never capped by `maxDiagnostics`; only `topDiagnostics` is. `topDiagnostics` is the solution-wide
top selection by severity, so a later project's errors always outrank an earlier project's warnings.

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
files on disk. The project is resolved and loaded the same way as the code navigation/editing
tools below (`IProjectLoader`): auto-discovery when `project` is omitted, `.sln` paths accepted,
exact-name project selection. Like `AnalyzeSolution`, reports compiler **and** analyzer diagnostics
(bundled Roslynator + the project's own analyzer references; disable with
`RoselineMCP:RunAnalyzers = false`).

#### Request

```typescript
{
  project?: string;      // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
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
[Tool Annotations](#tool-annotations)). The project is resolved and loaded the same way as the
code navigation/editing tools below (`IProjectLoader`): auto-discovery when `project` is omitted,
`.sln` paths accepted, exact-name project selection. A `previewOnly: false` call is also subject to
the [Write Confirmation](#write-confirmation) gate.

#### Request

```typescript
{
  ids: string[];         // Diagnostic IDs to fix (required, at least one)
  project?: string;      // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
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
  changedFiles: string[];    // Solution-root-relative paths (forward slashes; project-dir-relative when no .sln) of files that were modified
  patch: string;             // Unified diff across all changed files (headers use the same relative paths)
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

---

The remaining tools are **code navigation and editing** tools backed by Roslyn. They exist to save
tokens: rather than reading whole files into an agent's context, they return only the structure
(symbols, signatures, references, graphs) or a member-level diff. They all take an **optional**
`project` argument (a project name, a directory, a path to a `.csproj`, or a path to a `.sln`). When
`project` is omitted, RoselineMCP auto-discovers the solution/project from its working directory,
nearest level first — the working directory itself wins when it has exactly one candidate, then
each parent directory (up to 3) in order, then immediate subdirectories — returning a
`ValidationError` only when no candidate is found anywhere or a single level itself has multiple
candidates (a solution in the working directory is never made ambiguous by one further up the
tree, e.g. in a git worktree nested inside its main checkout). Local paths
only — unlike `AnalyzeSolution`, these do not accept a Git URL. When the project belongs to a
solution, the whole solution is loaded and symbol search/resolution spans **every project in it** —
a symbol declared only in a sibling project the requested project doesn't reference (e.g. a Tests
project) is still found — so cross-project references and renames are complete.
`ListDiagnostics` and `ApplyFixes` resolve and load their `project` through this same mechanism, so
every tool accepts the same references and reports the same solution-root-relative paths.

**Symbol references.** Wherever a tool takes a `symbol`/`method`/`type`, you may pass a simple name
(e.g. `GetUser`) or a fully-qualified name (e.g. `Acme.Users.UserService.GetUser`) to
disambiguate. If a simple name matches more than one symbol (including the same name declared in
two different projects), the tool returns a `ValidationError` listing the candidate fully-qualified
names.

### SearchSymbols

Find symbols by name pattern, or outline a single file. **Read-only.**

#### Request

```typescript
{
  project?: string;   // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  query?: string;     // Substring, or wildcard with * and ?. Omit to outline via `file`.
  file?: string;      // Restrict to a file (name or path suffix); outlines it when `query` is omitted
  kinds?: string[];   // Filter, e.g. ["class","interface","method","property","field","enum"]; also "type"/"member"
  max?: number;       // Maximum symbols (default: 50)
}
```

At least one of `query` or `file` is required (otherwise `ValidationError`).

#### Response

```typescript
{
  project: string;
  query: string | null;
  file: string | null;
  totalFound: number;    // Count before `max` was applied
  truncated?: boolean;   // Present (and `true`) only when the list was capped; omitted when not truncated
  symbols: Array<{
    name: string;
    fullName: string;
    kind: string;             // e.g. "class", "method", "property"
    signature: string;        // Already carries the accessibility keyword
    file: string | null;      // Solution-root-relative, forward slashes (e.g. "RoselineMCP/Services/Foo.cs")
    line: number | null;      // 1-based
  }>;
}
```

When outlining a single file (`file` set, `query` omitted), each symbol is returned as a **lean
projection** — `name`, `kind`, `signature`, `line`, and `containingType` (the *simple*, unqualified
type name) — omitting the per-symbol `file` (it is on the response) and `fullName` (reconstructable
from `containingType` + `name`). Solution-wide search returns the shape above, which no longer emits
`accessibility` (already inside `signature`) or `containingType` (already the prefix of `fullName`).
Null fields are omitted from the JSON throughout, and `truncated` is omitted when the list was not
capped.

### GetSymbolInfo

Declaration metadata, signature, and (optionally) the source of a single symbol. **Read-only.**

#### Request

```typescript
{
  project?: string;        // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  symbol: string;          // Simple or fully-qualified name
  includeSource?: boolean; // Include the declaration's source text (default: true)
}
```

#### Response

```typescript
{
  name: string;
  fullName: string;
  kind: string;
  modifiers?: string[];        // e.g. ["static","async"]; omitted when empty
  signature: string;           // Already carries the accessibility keyword
  baseTypes?: string[];        // Base-class chain (types only); omitted when empty
  interfaces?: string[];       // Directly-implemented interfaces (types only); omitted when empty
  documentation?: string;      // XML <summary> text, whitespace-collapsed; omitted when absent
  definitionFile?: string;     // Solution-root-relative, forward slashes; omitted when unknown
  definitionLine?: number;     // 1-based; omitted when unknown
  source?: string;             // Present only when includeSource is true
}
```

The `accessibility` field is not returned separately — it is already part of `signature`. Every
optional field above is omitted from the JSON when empty/absent, so a minimal symbol collapses to
just `name`, `fullName`, `kind`, and `signature`.

### FindReferences

Every use site of a symbol across the solution. **Read-only.**

#### Request

```typescript
{
  project?: string; // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  symbol: string;
  includeDefinition?: boolean; // Also include the declaration (default: false)
  max?: number;                // Maximum references (default: 100)
}
```

#### Response

```typescript
{
  symbol: string;
  fullName: string;
  totalReferences: number;   // Count before `max`
  truncated?: boolean;       // Present (and `true`) only when capped; omitted when not truncated
  references: Array<{
    file: string;    // Solution-root-relative, forward slashes
    line: number;    // 1-based
    snippet: string; // Trimmed source line
  }>;
}
```

### FindImplementations

Implementations of an interface/member, overrides of a virtual/abstract member, or derived types
of a class. **Read-only.**

#### Request

```typescript
{
  project?: string; // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  symbol: string;  // Interface, class, or member
  max?: number;    // Maximum results (default: 100)
}
```

#### Response

```typescript
{
  symbol: string;
  fullName: string;
  kind: string;
  totalFound: number;
  truncated?: boolean;              // Present (and `true`) only when capped; omitted when not truncated
  implementations: SymbolSummary[]; // Same shape as SearchSymbols' solution-wide `symbols`
}
```

### GetCallGraph

A depth-bounded caller and/or callee graph for a method, with cycle detection. **Read-only.**

#### Request

```typescript
{
  project?: string; // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  method: string;
  direction?: string; // "callers" (default) | "callees" | "both"
  depth?: number;     // Traversal depth, clamped to 1-3 (default: 1)
  max?: number;       // Maximum nodes expanded per direction (default: 50)
}
```

#### Response

```typescript
{
  method: string;
  fullName: string;
  direction: string;
  depth: number;
  callers?: CallGraphNode[]; // Present for "callers"/"both"
  callees?: CallGraphNode[]; // Present for "callees"/"both"
}

// CallGraphNode
{
  fullName: string;            // Parameter-qualified, with parameter TYPES as simple names
                               // (e.g. "RoselineMCP.Services.Foo.Bar(string, CancellationToken)"),
                               // so overloads stay distinct. Call get_symbol_info for the full signature.
  file: string | null;         // Solution-root-relative, forward slashes
  line: number | null;
  truncated?: boolean;         // Present (and `true`) only when a cycle or depth/budget stopped expansion; omitted otherwise
  children?: CallGraphNode[];  // Next level, when expanded
}
```

Each node deliberately omits the full `signature` (return type, parameter names, accessibility) to
keep the tree compact — the parameter-qualified `fullName` is enough to identify a method and
disambiguate overloads; fetch the full signature with `GetSymbolInfo` when needed.

### GetTypeHierarchy

A type's base-class chain, implemented interfaces, and/or derived types. **Read-only.**

#### Request

```typescript
{
  project?: string; // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  type: string;
  direction?: string; // "base" | "derived" | "both" (default)
  max?: number;       // Maximum derived types to return (default: 100)
}
```

#### Response

```typescript
{
  type: string;
  fullName: string;
  direction: string;
  baseTypes?: SymbolSummary[];      // Present for "base"/"both" — nearest base first, excluding object
  interfaces?: SymbolSummary[];     // Present for "base"/"both" — all implemented interfaces
  derivedTypes?: SymbolSummary[];   // Present for "derived"/"both" (capped at max)
  derivedTypesTruncated?: boolean;  // Present (and `true`) only when more derived types exist than were returned; omitted otherwise
}
```

### GetSymbolAtPosition

The symbol living at a `file:line(:column)` position — the bridge from a diagnostic, stack trace,
or grep hit to the symbol-name-based tools above. **Read-only.**

#### Request

```typescript
{
  project?: string; // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  file: string;     // File name or path suffix (same matching as SearchSymbols' `file`)
  line: number;     // 1-based
  column?: number;  // 1-based; omit to resolve the most relevant symbol on the line
}
```

Without a `column`, declarations on the line win over referenced symbols (so a method's declaration
line returns that method); with a `column`, whatever is bound at that exact position wins, falling
back to the enclosing declaration (a position on the modifiers or return type still means that
declaration). An out-of-range `line`/`column` is a `ValidationError`; an unknown file, or a position
with no symbol (e.g. a blank or comment line), is a `NotFoundError`.

#### Response

```typescript
{
  name: string;
  fullName: string;
  kind: string;               // e.g. "method", "class", "local"
  signature: string;          // Already carries the accessibility keyword
  containingType?: string;    // Simple (unqualified) container name; omitted for top-level symbols
  isDeclaration: boolean;     // True when the position sits on the symbol's own declaration
  documentation?: string;     // XML <summary> text, whitespace-collapsed; omitted when absent
  definitionFile?: string;    // Solution-root-relative, forward slashes; omitted when metadata-only
  definitionLine?: number;    // 1-based; omitted when metadata-only
}
```

### EditMember

Replace, add, or delete a single type member; returns a unified diff. **Defaults to preview mode**
(`previewOnly: true`) — no files are written unless you pass `previewOnly: false`
(`readOnlyHint: false`, `destructiveHint: true` as a worst-case annotation), and such a call is
subject to the [Write Confirmation](#write-confirmation) gate.

#### Request

```typescript
{
  project?: string;    // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  symbol: string;      // The member (replace/delete), or the container type (add)
  operation: string;   // "replace" | "add" | "delete"
  newSource?: string;  // C# member declaration — required for "replace" and "add"
  previewOnly?: boolean; // If true (default), only return a diff; pass false to write
}
```

#### Response

```typescript
{
  project: string;
  operation: string;
  target: string;          // Fully-qualified name of the member/type edited
  changedFiles: string[];  // Solution-root-relative path(s) modified (or that would be); forward slashes, project-dir-relative when no .sln
  patch: string;           // Unified diff
  previewOnly: boolean;
  applied: boolean;        // True only when previewOnly was false and there were changes
  notes: string[];
}
```

### RenameSymbol

Rename a symbol and update every reference across the solution using Roslyn's rename engine;
returns a unified diff. **Defaults to preview mode** (`previewOnly: true`)
(`readOnlyHint: false`, `destructiveHint: true` as a worst-case annotation). A `previewOnly: false`
call is subject to the [Write Confirmation](#write-confirmation) gate.

#### Request

```typescript
{
  project?: string;      // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  symbol: string;
  newName: string;       // Must be a valid C# identifier
  previewOnly?: boolean; // If true (default), only return a diff; pass false to write
}
```

#### Response

```typescript
{
  project: string;
  symbol: string;          // Fully-qualified name that was renamed
  newName: string;
  changedFiles: string[];  // Solution-root-relative paths (forward slashes; project-dir-relative when no .sln)
  patch: string;           // Unified diff across all changed files
  previewOnly: boolean;
  applied: boolean;
  notes: string[];
}
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
| `SearchSymbols` | `true` | `false` | `true` |
| `GetSymbolInfo` | `true` | `false` | `true` |
| `FindReferences` | `true` | `false` | `true` |
| `FindImplementations` | `true` | `false` | `true` |
| `GetCallGraph` | `true` | `false` | `true` |
| `GetTypeHierarchy` | `true` | `false` | `true` |
| `GetSymbolAtPosition` | `true` | `false` | `true` |
| `EditMember` | `false` | `true`\* | `false` |
| `RenameSymbol` | `false` | `true`\* | `false` |

\* The `destructiveHint` on `ApplyFixes`, `EditMember`, and `RenameSymbol` is a static, worst-case annotation: it is `true` because the
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
        string? project,           // null → auto-discovered via IProjectLoader
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
        string? project,            // null → auto-discovered via IProjectLoader
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

### ICodeNavigationService

Read-only structural/semantic navigation (backs the seven navigation tools).

```csharp
public interface ICodeNavigationService
{
    Task<SymbolSearchResponse> SearchSymbolsAsync(
        string? project, string? query, string? file, string[]? kinds, int max,
        CancellationToken cancellationToken = default);

    Task<SymbolInfoResponse> GetSymbolInfoAsync(
        string? project, string symbol, bool includeSource,
        CancellationToken cancellationToken = default);

    Task<SymbolAtPositionResponse> GetSymbolAtPositionAsync(
        string? project, string file, int line, int? column,
        CancellationToken cancellationToken = default);

    Task<ReferencesResponse> FindReferencesAsync(
        string? project, string symbol, bool includeDefinition, int max,
        CancellationToken cancellationToken = default);

    Task<ImplementationsResponse> FindImplementationsAsync(
        string? project, string symbol, int max,
        CancellationToken cancellationToken = default);

    Task<CallGraphResponse> GetCallGraphAsync(
        string? project, string method, string direction, int depth, int max,
        CancellationToken cancellationToken = default);

    Task<TypeHierarchyResponse> GetTypeHierarchyAsync(
        string? project, string type, string direction,
        CancellationToken cancellationToken = default);
}
```

### ICodeEditService

Surgical, symbol-aware edits (backs `EditMember` and `RenameSymbol`). Preview by default — writes
only when `previewOnly` is `false`.

```csharp
public interface ICodeEditService
{
    Task<EditMemberResponse> EditMemberAsync(
        string? project, string symbol, string operation, string? newSource, bool previewOnly,
        CancellationToken cancellationToken = default);

    Task<RenameSymbolResponse> RenameSymbolAsync(
        string? project, string symbol, string newName, bool previewOnly,
        CancellationToken cancellationToken = default);
}
```

### IProjectLoader

Loads a project (and its solution, when found) into a workspace for navigation/edits. Accepts
a project name, directory, `.csproj` path, or `.sln` path; when `project` is `null`/whitespace the
solution/project is auto-discovered from the working directory, nearest level first (the working
directory itself, then each parent up to 3 in order, then immediate subdirectories — throwing
`ArgumentException` only when nothing is found or a single level itself has multiple candidates).
In production the interface resolves to `CachingProjectLoader`, a
decorator that reuses the loaded workspace across calls and reloads it whenever the solution's files
change on disk (disable via `RoselineMCP:WorkspaceCache = false`); returned handles should always be
disposed — disposal releases owned workspaces and is a no-op for cached, shared ones.

```csharp
public interface IProjectLoader
{
    Task<LoadedProject> LoadAsync(string? project, CancellationToken cancellationToken = default);
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

### SymbolSummary

The compact per-symbol shape shared by `SearchSymbols`, `FindImplementations`, and
`GetTypeHierarchy` (JSON property names in `camelCase`). Every nullable field below is omitted from
the JSON when null.

```csharp
public class SymbolSummary
{
    public string Name { get; set; }
    public string? FullName { get; set; }       // Omitted in the single-file outline
    public string Kind { get; set; }            // "class", "method", ...
    public string Signature { get; set; }       // Already carries the accessibility keyword
    public string? File { get; set; }           // Solution-root-relative; omitted in the outline
    public int? Line { get; set; }              // 1-based
    public string? ContainingType { get; set; } // Emitted ONLY in the single-file outline, as the simple type name
}
```

**Solution-wide** results (`SearchSymbols` with a `query`, `FindImplementations`, and the
`GetTypeHierarchy` base/interface/derived lists) emit `name`, `fullName`, `kind`, `signature`,
`file`, and `line` — never `accessibility` (already inside `signature`) nor `containingType`
(already the prefix of `fullName`). The **single-file outline** (`SearchSymbols` with `file`,
`query` omitted) instead emits `name`, `kind`, `signature`, `line`, and `containingType` (the
simple, unqualified type name), dropping `file` and `fullName`.

### Navigation & edit response models

All navigation/edit responses use `camelCase` JSON property names. Their fields are documented in
the [MCP Tools](#mcp-tools) sections above; the C# types are:

- `SymbolSearchResponse` — `project`, `query`, `file`, `totalFound`, `truncated?` (omitted when not capped), `symbols: SymbolSummary[]`
- `SymbolInfoResponse` — `name`, `fullName`, `kind`, `signature`, and (omitted when empty/absent) `modifiers[]`, `baseTypes[]`, `interfaces[]`, `documentation`, `definitionFile`, `definitionLine`, `source` (no `accessibility` — it is inside `signature`)
- `SymbolAtPositionResponse` — `name`, `fullName`, `kind`, `signature`, `isDeclaration`, and (omitted when empty/absent) `containingType` (simple name), `documentation`, `definitionFile`, `definitionLine`
- `ReferencesResponse` — `symbol`, `fullName`, `totalReferences`, `truncated?` (omitted when not capped), `references: ReferenceLocation[]` (`file`, `line`, `snippet`)
- `ImplementationsResponse` — `symbol`, `fullName`, `kind`, `totalFound`, `truncated?` (omitted when not capped), `implementations: SymbolSummary[]`
- `CallGraphResponse` — `method`, `fullName`, `direction`, `depth`, `callers?`, `callees?` of `CallGraphNode` (`fullName` with simple parameter-type names, `file`, `line`, `truncated?`, `children?`; no per-node `signature`)
- `TypeHierarchyResponse` — `type`, `fullName`, `direction`, `baseTypes?`, `interfaces?`, `derivedTypes?` (all `SymbolSummary[]`), `derivedTypesTruncated?` (omitted when not truncated)
- `EditMemberResponse` — `project`, `operation`, `target`, `changedFiles[]`, `patch`, `previewOnly`, `applied`, `notes[]`
- `RenameSymbolResponse` — `project`, `symbol`, `newName`, `changedFiles[]`, `patch`, `previewOnly`, `applied`, `notes[]`

## Error Handling

Tools never throw to the MCP protocol layer — every failure (validation, not-found, cancellation,
timeout, or unexpected exception) is caught and returned as a JSON object with a stable, closed
set of `type` values. Raw CLR exception type names (`ex.GetType().Name`) are **never** surfaced;
every failure is classified first.

### Error Response Format

A failure is reported as the envelope's `ok: false` branch, with everything nested under `error`:

```typescript
{
  ok: false;         // Always false on failure. Note: this is in-band — the MCP-protocol isError
                     // flag stays false, since the tool never throws to the protocol layer.
  error: {
    type: string;     // One of: "ValidationError" | "NotFoundError" | "AnalysisError" |
                       // "CancelledError" | "TimeoutError" | "InternalError"
    message: string;  // Human-readable message. For InternalError, this is always the fixed string
                       // "An unexpected internal error occurred. Check the server logs for details."
                       // — the real exception message/stack trace is logged server-side only, never returned.
    hint?: string;    // Present on some ValidationError responses: suggests the concrete fix
                       // (e.g. the accepted enum values, or which tool to call first)
    correlationId: string;  // Per-invocation GUID, always present. Lets a user reporting a failure
                             // hand you one ID that ties back to the full server-side log entry for
                             // that call (see "Tracing Individual Tool Calls" in the README).
  };
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
  "ok": false,
  "error": {
    "type": "NotFoundError",
    "message": "Solution file not found: /path/to/missing.sln",
    "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21"
  }
}
```

```json
{
  "ok": false,
  "error": {
    "type": "ValidationError",
    "message": "No diagnostic IDs provided.",
    "hint": "Call ListDiagnostics first to discover fixable diagnostic IDs for this project, then pass one or more of them, e.g. ids: [\"RCS1213\"].",
    "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21"
  }
}
```

```json
{
  "ok": false,
  "error": {
    "type": "TimeoutError",
    "message": "Operation timed out after 120000ms",
    "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21"
  }
}
```

## Performance Characteristics

There is no built-in request rate limiting — callers are responsible for their own throttling if
needed. Each MCP tool call is bounded by a configurable wall-clock timeout instead (see
`RoselineMCP:DefaultTimeout` above and `docs/ARCHITECTURE.md`). Rough complexity per call:

- **AnalyzeSolution**: proportional to the number of projects times diagnostics per project;
  projects within a solution are analyzed concurrently, bounded by the processor count. Running
  analyzers (the default) adds a `CompilationWithAnalyzers` pass per project — noticeably slower
  than compiler-only on large solutions; set `RoselineMCP:RunAnalyzers = false` when only
  compiler diagnostics are needed
- **ListDiagnostics**: proportional to diagnostics in the target project (plus one analyzer pass
  unless `RoselineMCP:RunAnalyzers = false`)
- **ApplyFixes**: proportional to files touched times diagnostics fixed per file; when the fix
  provider supports FixAll at project scope (most built-in and Roslynator fixers do), all
  occurrences of a diagnostic ID are fixed in a single batch pass. Providers without FixAll
  support fall back to occurrence-by-occurrence fixing, re-analyzing the solution after every
  applied fix
- **CreatePatch**: proportional to the number of lines in the two inputs

### Recommendations

1. **Use filtering**: Apply `include`/`exclude` (substring match) to reduce `AnalyzeSolution` scope
2. **Limit results**: Use `maxDiagnostics`/`max` to cap response size
3. **Preview first**: Leave `ApplyFixes`' `previewOnly` at its default (`true`) to review changes
   before passing `false` to write them
4. **Batch operations**: Group related diagnostic IDs into a single `ApplyFixes` call

## Supported Diagnostic IDs

Diagnostics come from three sources, all surfaced through the same tools: the C# compiler, the
**bundled Roslynator analyzers** (shipped inside RoselineMCP as an `analyzers/` folder next to
`RoselineMCP.dll` and executed via `CompilationWithAnalyzers`), and **the target project's own
analyzer references** (whatever the analyzed repository has installed — StyleCop, custom rules,
…). Fixability is always determined at runtime: `suggestedFixableIds` reflects the code fix
providers actually discovered from the Roslyn built-ins and the bundled Roslynator fixer
assemblies. Setting `RoselineMCP:RunAnalyzers` to `false` limits everything to compiler
diagnostics.

### Roslyn (CS/BC)
- CS0168: Variable declared but never used
- CS0219: Variable assigned but never used
- CS0649: Field never assigned
- CS1591: Missing XML documentation
- And 1000+ more...

### Roslynator (RCS)
Bundled and executed by default — reported by `AnalyzeSolution`/`ListDiagnostics` and fixable via
`ApplyFixes` when Roslynator ships a fixer for the rule (most rules; ~440 fixable IDs are
discovered at runtime). Examples:
- RCS1001: Add braces
- RCS1036: Remove unnecessary blank line
- RCS1104: Simplify conditional expression
- RCS1213: Remove unused member declaration
- And 500+ more... (rules disabled by default in Roslynator, e.g. most RCS0xxx formatting rules,
  stay disabled unless the analyzed project enables them via `.editorconfig`)

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