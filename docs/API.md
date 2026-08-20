# RoselineMCP API Documentation

Complete API reference for RoselineMCP tools and services.

## Table of Contents

- [MCP Tools](#mcp-tools)
  - [Compile Verification](#compile-verification)
  - [Write Confirmation](#write-confirmation)
  - [AnalyzeSolution](#analyzesolution)
  - [ListDiagnostics](#listdiagnostics)
  - [ApplyFixes](#applyfixes)
  - [CheckCompilation](#checkcompilation)
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
  { "ok": false, "error": { "type": "...", "message": "...", "correlationId": "...", "resolvedPath": "..." } }
  ```

  `resolvedPath` is **optional**, and its absence is meaningful: it names the absolute
  `.sln`/`.csproj` that answered the call, and is **omitted entirely — never `""` — when the failure
  happened before any project was resolved**. "Never resolved" and "resolved to nothing" are
  different claims, so they are never conflated on the wire.

Each tool's **Response** schema shown below describes the shape of the `data` object (the success
payload), not the envelope. The tools set `UseStructuredContent = true`, so the same object is also
delivered as MCP `structuredContent` alongside an advertised `outputSchema`.

### Compile Verification

Applies to the three write-capable tools — [`ApplyFixes`](#applyfixes), [`EditMember`](#editmember)
and [`RenameSymbol`](#renamesymbol) — and is the whole payload of
[`CheckCompilation`](#checkcompilation).

Before anything reaches disk, the candidate change is compiled **in memory** and its compiler
diagnostics are diffed against the same scope's diagnostics before the change. When the change
*introduces* compiler errors, the write is **refused**: the call still succeeds (`ok: true`), but
`applied` comes back `false`, nothing is written, and the response carries the diff *and* the
introduced errors. Pass `allowIntroducedErrors: true` to write anyway.

**The gate is `introduced`, never `compiles`.** A repository that was already broken before the edit
reports `compiles: false` with an empty `introduced`, and the write proceeds — refusing there would
make RoselineMCP unusable on exactly the branches an agent is sent to fix. `preexisting` counts
those errors so an agent does not mistake them for its own.

Verification runs **before** the [Write Confirmation](#write-confirmation) prompt. Asking a human to
approve a write the gate is about to refuse spends the one thing the elicitation costs — their
attention — and trains them to click through it; a refusal is also strictly more informative than a
decline, since it carries the errors as well as the diff.

Only **compiler** diagnostics are computed, never analyzers: analyzers cost several times a bare
compile and would turn a build gate into a style gate. Use [`ListDiagnostics`](#listdiagnostics) for
the analyzer view.

#### The `verification` object

```typescript
{
  resolvedPath?: string;   // check_compilation only; omitted when nested in a write response
  compiles?: boolean;      // absolute: does the scope compile? omitted when nothing was compiled
  errors?: DiagnosticDetail[];      // absolute mode only (check_compilation); omitted when empty
  introduced?: DiagnosticDetail[];  // errors this change added — a non-empty list refuses the write
  resolved?: DiagnosticDetail[];    // errors this change removed
  preexisting?: number;    // errors that were already there; omitted when zero
  omitted?: number;        // diagnostics dropped to honor `max`; omitted when zero
  scope?: string[];        // projects compiled: the changed ones plus their transitive dependents
  scopeComplete: boolean;  // always present — see below
  notes?: string[];        // chiefly why scopeComplete is false
}
```

Every collection is omitted from the wire when empty and every counter when zero: the verdict rides
on every edit, and always-present fields would spend tokens on the overwhelmingly common
"nothing to report" case. `scopeComplete` is the deliberate exception and is **always** emitted,
because `false` is its informative value and an absent field would be indistinguishable from a
full-coverage gate.

#### Scope

The compiled set is the **changed projects plus their transitive dependents**. File-only scope
misses the cross-project breakage agents fail at most; whole-solution scope pays to compile projects
the change cannot possibly affect. `check_compilation`, which has no before-state, compiles the
whole loaded solution.

`scopeComplete: false` means the workspace could not prove it holds every dependent — a bare
`.csproj` was loaded with no containing solution. The write still proceeds, but the caller is told
the gate was partial rather than handed a false green. Pass the `.sln` path as `project` to close it.

#### Truncation

All four tools accept `max` (default **20**) and report `omitted`. A rename that breaks a public
member of a base project produces thousands of binding errors; unbounded, the refusal would cost
more tokens than the `dotnet build` output it replaces.

#### Guarantee boundary

The promise is precise, and deliberately narrower than it could be made to sound:

> **The verified change set compiles, and no refused edit is ever written.**

It is **not** "the working tree always compiles after any outcome". Both multi-file writers apply
changes **file by file**, so a failure partway through a twelve-file rename leaves some files written
and some not — the tree is then in a state neither the baseline nor the candidate describes. That is
a documented boundary, covered by a test
(`CodeEditServiceTests.RenameSymbol_Multi_File_Write_Is_Not_Atomic`), not an aspiration.

### Write Confirmation

Applies to the three write-capable tools — [`ApplyFixes`](#applyfixes),
[`EditMember`](#editmember) and [`RenameSymbol`](#renamesymbol). All three default to
`previewOnly: true` and write nothing unless the caller passes `previewOnly: false`.

Behind that opt-in sits a second, **best-effort** guard: when `previewOnly: false` is passed, the
server sends an MCP `elicitation/create` asking the connected client to confirm before writing.

A confirmation is sent only once there is a change that is **valid, non-empty and compiles**. Three
outcomes short-circuit before the prompt for the same reason: asking a human to approve a write that
was never going to happen spends the one thing the elicitation costs — their attention.

- **Invalid input** — e.g. `EditMember`'s `newSource` missing on `replace`/`add` — fails with the
  ordinary validation error before the write path is even entered.
- [Compile Verification](#compile-verification) **refuses** a change that would introduce compiler
  errors: the call still succeeds (`ok: true`), but nothing is written.
- **No changes** — a phase-1 preview that produces no changes at all (a rename to the symbol's own
  name, or a `replace`/`add` whose `newSource` matches what is already there) returns that preview
  directly; the tool's own note (`"No changes were produced by the edit."`,
  `"Rename produced no changes."`) survives unchanged.

The prompt **names the concrete `.sln` or `.csproj` the write resolved to** — an absolute path, never
a placeholder — whether the caller passed `project`, left it out, or passed an empty string:

> Rename 'Foo' to 'Bar' across the solution of '/Users/me/src/Acme/Acme.sln' and write the changes to disk?

The path is resolved by the same function, against the same base directory, that the loader uses —
and the **resolved path is what the write is then performed against**, rather than the argument the
caller passed. That second part is what makes the guarantee hold across the human round-trip: the
target is resolved once, before the prompt, so a file system that changes while someone is deciding
cannot leave them approving one solution and the server writing to another. Because `project` is
optional and auto-discovery walks the working directory, its parents and its immediate
subdirectories, this is the one thing that lets a caller notice a server launched from an unexpected
directory is about to write to a solution they did not intend.

Naming the target is not quite the same as naming the **scope**. [`ApplyFixes`](#applyfixes) is
project-scoped: when the resolved target is a solution it fixes a **single** project inside it — the
anchor `ProjectLoader` selects (the C# project whose file name matches the `.sln`, otherwise the
first C# project Roslyn enumerated) — and only that project's documents are rewritten. Its prompt
says so rather than implying a solution-wide write:

> Apply code fixes for 2 diagnostic ID(s) to the primary project of '/Users/me/src/Acme/Acme.sln' and write the changes to disk?

When the resolved target is already a `.csproj`, that project *is* the whole scope and the sentence
names it directly, with no qualifier. The prompt deliberately does not name *which* project the
anchor will be: that answer requires loading an MSBuild workspace, which would happen before the
human has agreed to anything and would have to be re-derived after the round-trip — reopening the
window that resolving-once closes. Note that "one project's documents" is a statement about
documents, not about who *sees* the change: a file linked into several projects
(`<Compile Include="..\Shared\Config.cs" Link="Config.cs"/>`) is one file on disk, so fixing it in
the anchor project changes what every project linking it compiles.

The other two write tools are not described by the sentence above, and they differ from each other.
[`EditMember`](#editmember) has the narrowest scope of the three: it resolves one declaration and
rewrites **exactly one file**. Its prompt says that outright rather than letting the target stand in
for the scope, and unlike `ApplyFixes`' qualifier it does not branch on the target's extension,
because the write is one file whether the target is a `.sln` or a `.csproj`:

> Write the 'delete' of member 'Foo.Bar' to disk? Exactly one file is rewritten — the declaration it resolves to, anywhere in the code loaded from '/Users/me/src/Acme/Acme.sln'.

Two words there are load-bearing. **"Exactly one file"** is the part the code guarantees:
`CodeEditService` resolves a single declaration and writes once. **"loaded from"**, rather than *in*,
is the other: a `.csproj` target does not bound the write, because `ProjectLoader` opens the
containing solution when it finds one and symbol resolution spans every project in it — so the file
rewritten can belong to a sibling project the caller never named. For the same reason the prompt does
not claim *the* file declaring the symbol: a partial type has several declarations, and Roslyn picks
one. Which file it lands on stays unnamed, for the reason `ApplyFixes` does not name its anchor
project: resolving it means loading an MSBuild workspace before the human has agreed to anything.

On `add` the sentence names a **type** instead of a member — `symbol` is the container type there,
and the member being added is declared nowhere yet:

> Write the 'add' of a member to type 'Acme.OrderService' to disk? Exactly one file is rewritten — the declaration it resolves to, anywhere in the code loaded from '/Users/me/src/Acme/Acme.sln'.

[`RenameSymbol`](#renamesymbol) carries no scope qualifier at all — it is a genuinely solution-wide
Roslyn operation that can rewrite files across every project in the loaded solution, so naming the
solution is already exact. (`ApplyFixes` also drops its qualifier when the target is a `.csproj`, for
the same reason: there the target *is* the scope.)

All three sentences above are rendered in **one place**, from structured inputs: a tool names its
scope from a closed vocabulary (`WriteScope` — `PrimaryProjectOf`, `SingleFile`, `WholeSolution`) and
hands over the values, and `WritePrompt.Render` composes the sentence. A tool no longer writes its
own prompt, so the three phrasings cannot drift apart again and a fourth write tool inherits wording
its siblings already agreed to.

That is also what makes the sanitising structural. `symbol` and `newName` are free-form caller input;
when each tool interpolated them itself, the injection had already happened by the time anything
shared saw the string, and a crafted `symbol` could close the quoted run and append a second,
plausible sentence naming a project the write would never touch. Rendering from values instead means
every caller-supplied one passes through the same filter.

That filter is a **whitelist**: a symbol reference may contain letters, digits and
`` . _ < > , @ ` : + ``, and everything else is dropped, then the result is capped with a mid-string
elision (`Acme.…Service`). Whitelisting rather than escaping is deliberate — the reader is a human,
and the set of characters that *look* like a space or a quote is open-ended (U+2800 and U+3164 render
blank without being whitespace; a caller-supplied U+2019 reads as the frame's own quote). An ordinary
C# symbol contains none of them and renders unchanged. The **target** is deliberately left verbatim,
because it has to stay checkable against the file system; see
[SECURITY.md](../SECURITY.md) for the one residual that follows from that.

Resolution is pure path work — no MSBuild workspace is loaded — and is far cheaper than the load
that follows, but it is not free: a bare project **name** that matches neither a file nor a directory
falls back to a recursive `*.csproj` scan of the working directory. Nothing on a path that will not
send a prompt pays that cost, since none of them resolve at all.

| Situation | Result |
|---|---|
| Client accepts | The write proceeds; `previewOnly` comes back `false`. |
| Client **declines** | The call is downgraded to a preview — nothing is written, `previewOnly` comes back `true`, and `notes[]` gains `"Write declined via client confirmation; returned a preview only (no files were modified)."` |
| Client is asked and **never answers** | After `RoselineMCP:ConfirmDestructiveWritesTimeout` (default `300000`, 5 minutes) the server stops waiting and downgrades the call to a preview — nothing is written, `previewOnly` comes back `true`, and `notes[]` gains `"Write confirmation timed out; returned a preview only (no files were modified). Set RoselineMCP:ConfirmDestructiveWrites=false on unattended hosts that should write without a human, or raise RoselineMCP:ConfirmDestructiveWritesTimeout."` |
| Client does not support elicitation, or the round-trip fails | No confirmation is possible, so the explicit opt-in stands and the write proceeds. A client that never negotiated elicitation is detected from its capabilities, so no prompt is built and no target is resolved. |
| `RoselineMCP:ConfirmDestructiveWrites` is `false` | **No elicitation is sent at all** (as opposed to one being auto-accepted); the write proceeds. The prompt is not even built, so no target is resolved. |
| The write target **cannot be resolved** — auto-discovery finds nothing or several candidates, an explicit `project` matches nothing, or a named directory cannot be read | **No elicitation is sent**; the call returns its ordinary failure envelope (`ok: false`, a `ValidationError`, `NotFoundError` or `AnalysisError` — see [Error Handling](#error-handling)). A write that cannot be targeted fails before a human is asked, rather than spending their answer on a call that was going to fail anyway. |

Silence is deliberately *not* consent: a client that **cannot** be asked justifies honoring the
explicit opt-in, but one that was asked and said nothing does not. The timeout therefore removes the
hang without weakening the guard — the only way to write without a human remains an explicit
operator decision. Its clock is deliberately **not** `RoselineMCP:DefaultTimeout`: that is an
analysis budget, and a human reading a real diff may legitimately exceed it. Set the timeout to `0`
or less to remove the bound entirely and wait indefinitely, as before this option existed.

`DefaultTimeout`'s own clock starts only **after** the confirmation resolves, so the analysis budget
measures analysis rather than analysis plus however long the human took. A confirmation that takes
longer than `DefaultTimeout` therefore still produces the write (if accepted) or the preview (if
declined or timed out) — never a `TimeoutError`.

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

A `previewOnly: true` call is unaffected throughout: it reaches no disk, so no confirmation is sent,
no prompt is built and no target is resolved.

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
  resolvedPath: string;               // Absolute .sln/.csproj that was actually loaded
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

**A `.sln` target fixes one project, not the solution.** Unlike the navigation tools, whose search
spans every project in the loaded solution, `ApplyFixes` acts on a single project: the anchor
`ProjectLoader` selects (the C# project whose file name matches the `.sln`, otherwise the first C#
project Roslyn enumerated). Diagnostics in the solution's other projects are neither fixed nor
reported as skipped, so `changedFiles` being short is not evidence they were clean. The `project`
field of the response names the one that was fixed, and `resolvedPath` names the target it was
chosen from. Pass an explicit `.csproj` to fix a specific project. (The confirmation prompt says the
same thing, but it is only shown on a `previewOnly: false` call to an eliciting client — see
[Write Confirmation](#write-confirmation).)

#### Request

```typescript
{
  ids: string[];         // Diagnostic IDs to fix (required, at least one)
  project?: string;      // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  previewOnly?: boolean; // If true (the default), only generate a diff — no files written. Pass false to apply.
  allowIntroducedErrors?: boolean; // If false (default), fixes that introduce compiler errors are refused
  max?: number;          // Max diagnostics per verification list (default 20); the rest are counted in `omitted`
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
  resolvedPath: string;      // Absolute .sln/.csproj that was actually loaded
  fixedCount: number;        // Total number of individual fixes applied across all requested IDs
  fixersApplied: string[];   // Diagnostic IDs that were successfully fixed at least once
  changedFiles: string[];    // Solution-root-relative paths (forward slashes; project-dir-relative when no .sln) of files that were modified
  patch: string;             // Unified diff across all changed files (headers use the same relative paths)
  notes: string[];           // Per-ID status messages: skipped (no provider/no diagnostics), errors, or "applied N fixes to M files" / "Preview mode - no changes were saved to disk"
  previewOnly: boolean;      // Echoes back whether the caller asked for a preview
  applied: boolean;          // True only when previewOnly was false, there were changes, and verification did not refuse
  verification?: object;     // The compiler's verdict — see Compile Verification
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

### CheckCompilation

Answers *"does this compile right now, and what broke"* against on-disk state — the replacement for
a `dotnet build` round trip in an agent's edit loop. **Read-only** (`readOnlyHint: true`,
`destructiveHint: false`): it never touches disk.

It answers about whatever is on disk, whoever wrote it, so it serves agents that never call
RoselineMCP's write tools at all. The saving comes from the warm `MSBuildWorkspace` the server
already holds: the first call of a session pays a cold load, every call after it reuses an
incremental Roslyn compilation.

Compiler diagnostics only. For an exploratory inventory — analyzer diagnostics, severity statistics,
which IDs are auto-fixable — use [`ListDiagnostics`](#listdiagnostics), the slower and broader tool.
Rule of thumb: **`check_compilation` answers "is it still building?", `list_diagnostics` answers
"what should I clean up?"**

#### Request

```typescript
{
  project?: string;  // Optional — name, directory, .csproj, or .sln; auto-discovered from cwd if omitted
  max?: number;      // Max errors to return (default 20); the rest are counted in `omitted`
}
```

#### Response

The [`verification` object](#the-verification-object) itself, with `resolvedPath` set and the
absolute-mode fields populated (`compiles`, `errors`); `introduced`/`resolved` are absent, since
there is no before-state to compare against.

```typescript
{
  resolvedPath: string;    // Absolute .sln/.csproj that was actually loaded
  compiles: boolean;
  errors?: DiagnosticDetail[];  // omitted when the scope compiles
  omitted?: number;
  scope?: string[];
  scopeComplete: boolean;
  notes?: string[];
}
```

#### Example

```bash
mcp call checkCompilation '{ "max": 20 }'
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

**Working in a git worktree.** Auto-discovery is anchored to **the server's** working directory —
the directory the MCP client launched RoselineMCP in — not the agent's. They differ whenever work
happens in a git worktree (e.g. `.claude/worktrees/<name>`): the worktree sits below the level
walk's reach, so an omitted `project` resolves the main checkout instead. Two checkouts of the same
repository are otherwise indistinguishable in a response — same project name, same
solution-root-relative paths — so pass an absolute `.sln`/`.csproj` path to target a specific
checkout, and check `resolvedPath` in the response to confirm which one answered.

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
  resolvedPath: string;  // Absolute .sln/.csproj that was actually loaded
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
  resolvedPath: string;        // Absolute .sln/.csproj that was actually loaded
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
just `resolvedPath`, `name`, `fullName`, `kind`, and `signature`.

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
  resolvedPath: string;      // Absolute .sln/.csproj that was actually loaded
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
  resolvedPath: string;             // Absolute .sln/.csproj that was actually loaded
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
  resolvedPath: string;      // Absolute .sln/.csproj that was actually loaded
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
  resolvedPath: string;             // Absolute .sln/.csproj that was actually loaded
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
  resolvedPath: string;       // Absolute .sln/.csproj that was actually loaded
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
  allowIntroducedErrors?: boolean; // If false (default), an edit that introduces compiler errors is refused
  max?: number;          // Max diagnostics per verification list (default 20); the rest are counted in `omitted`
}
```

#### Response

```typescript
{
  project: string;
  resolvedPath: string;    // Absolute .sln/.csproj that was actually loaded
  operation: string;
  target: string;          // Fully-qualified name of the member/type edited
  changedFiles: string[];  // Solution-root-relative path(s) modified (or that would be); forward slashes, project-dir-relative when no .sln
  patch: string;           // Unified diff
  previewOnly: boolean;
  applied: boolean;        // True only when previewOnly was false, there were changes, and verification did not refuse
  verification?: object;   // The compiler's verdict — see Compile Verification
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
  allowIntroducedErrors?: boolean; // If false (default), a rename that introduces compiler errors is refused
  max?: number;          // Max diagnostics per verification list (default 20); the rest are counted in `omitted`
}
```

#### Response

```typescript
{
  project: string;
  resolvedPath: string;    // Absolute .sln/.csproj that was actually loaded
  symbol: string;          // Fully-qualified name that was renamed
  newName: string;
  changedFiles: string[];  // Solution-root-relative paths (forward slashes; project-dir-relative when no .sln)
  patch: string;           // Unified diff across all changed files
  previewOnly: boolean;
  applied: boolean;        // True only when previewOnly was false, there were changes, and verification did not refuse
  verification?: object;   // The compiler's verdict — see Compile Verification
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
| `CheckCompilation` | `true` | `false` | `true` |
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
        bool previewOnly = false,  // NOTE: this C# default is `false`; the MCP `ApplyFixes`
                                    // tool always passes an explicit value and defaults to
                                    // `true` at that boundary — see the ApplyFixes tool section above.
        bool allowIntroducedErrors = false,
        int max = 20,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default);
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
        bool allowIntroducedErrors = false, int max = 20,
        CancellationToken cancellationToken = default);

    Task<RenameSymbolResponse> RenameSymbolAsync(
        string? project, string symbol, string newName, bool previewOnly,
        bool allowIntroducedErrors = false, int max = 20,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default);
}
```

### IVerificationService

Compiles a candidate `Solution` in memory and reports what the change did to the compiler's verdict
— the machinery behind the write tools' refusal gate and behind `CheckCompilation`. Compiler
diagnostics only, by design.

```csharp
public interface IVerificationService
{
    Task<VerificationVerdict> VerifyAsync(
        Solution? baseline,   // null → absolute verdict (check_compilation)
        Solution candidate,
        int max = 20,
        CancellationToken cancellationToken = default);
}
```

`touched` is deliberately **not** a parameter: the changed-project set is derived internally from
`candidate.GetChanges(baseline)`, because a caller that under-reported it would silently narrow the
scope and let the gate pass broken code.

The production registration passes `DiagnosticComputationService.CompilerOnly`, never the
analyzer-aware implementation. Per-project results are cached, keyed by the project's **file path**
(stable across reloads, unlike the `ProjectId` GUIDs a reload mints afresh) plus **both** its
dependent semantic version and its dependent version — the semantic version alone is blind to
method-body edits, which is exactly how a write introduces a compiler error. Entries hold projected
`DiagnosticDetail` values only, never `Diagnostic`/`Compilation`/`Location`, which would root a
`SyntaxTree` into a workspace whose memory is never returned to the OS.

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
    public bool Applied { get; set; }                  // JSON: "applied"
    public VerificationVerdict? Verification { get; set; }  // JSON: "verification"
}
```

### VerificationVerdict

```csharp
public class VerificationVerdict
{
    public string? ResolvedPath { get; set; }          // check_compilation only
    public bool? Compiles { get; set; }                // absolute over the scope; null = nothing compiled
    public List<DiagnosticDetail>? Errors { get; set; }        // absolute mode
    public List<DiagnosticDetail>? Introduced { get; set; }    // the gate
    public List<DiagnosticDetail>? Resolved { get; set; }
    public int Preexisting { get; set; }
    public int Omitted { get; set; }
    public List<string>? Scope { get; set; }
    public bool ScopeComplete { get; set; }            // always serialized
    public List<string>? Notes { get; set; }
}
```

See [Compile Verification](#compile-verification) for the semantics, the scope rule and the
guarantee boundary.

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
    resolvedPath?: string;  // The absolute .sln/.csproj that answered this call — the same value the
                             // success responses carry. Present whenever the failure happened AFTER a
                             // project was resolved, including for InternalError (the message is
                             // scrubbed; the path is not secret). OMITTED — never "" — when nothing
                             // was ever resolved: every ValidationError and CancelledError, and any
                             // failure in loading itself. See "Which checkout answered?" below.
  };
}
```

#### Which checkout answered?

Two checkouts of one repository — a git worktree and its main checkout — are otherwise reported
identically: same project name, same solution-root-relative file paths. When `project` is omitted,
RoselineMCP auto-discovers from **the server process's** working directory, which is not the
agent's; a worktree under `.claude/worktrees/<name>` sits below the discovery walk's reach, so the
main checkout answers instead.

On the success path that mismatch surfaces as a `resolvedPath` you did not expect. On the failure
path it surfaces as `NotFoundError: Symbol not found: 'X'` — and without this field there is nothing
in the response to tell that apart from "the symbol does not exist". Compare `error.resolvedPath`
against the checkout you meant, and pass an absolute path as `project` to target a specific one.

### Error Types

| `type` | Meaning | Example trigger |
|--------|---------|------------------|
| `ValidationError` | Caller-supplied input was missing, malformed, or otherwise invalid | Unrecognized `severity` string; `ApplyFixes` called with an empty `ids` array |
| `NotFoundError` | The requested solution, project, or file could not be located | `FileNotFoundException`, `DirectoryNotFoundException` |
| `AnalysisError` | Failure while analyzing, building, or fetching the target | MSBuild workspace load failure, Git clone failure/timeout, permission denied — a read-only source file the write tools cannot open, or a directory the server cannot read |
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

No `resolvedPath` above: the solution was never loaded, so no checkout answered. Contrast a lookup
that failed *inside* a loaded solution — the field names the checkout that was searched:

```json
{
  "ok": false,
  "error": {
    "type": "NotFoundError",
    "message": "Symbol not found: 'UserService'. Use search_symbols to discover exact names in this solution.",
    "correlationId": "3fa1c2b4e6a94f1c8b2d1e0a5c7d9f21",
    "resolvedPath": "/Users/me/repo/MySolution.sln"
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