# MCP Server: Code Analysis & Automated Fixes

**Tagline:** Let AI run safe, scripted code cleanups on your C# repos and hand you a review‑ready patch.

---

## Problem

Teams want automated help to surface code smells and apply proven fixers, but they need **guardrails**: no arbitrary edits, deterministic outputs, and developer‑controlled review (patches/PRs).

## Solution

An **MCP server** exposing code‑quality tools powered by **Roslyn analyzers & code fix providers** and diffed with **DiffPlex**. AI clients (Copilot/agents) can:

* analyze a solution, list diagnostics by rule, and apply selected fixers;
* receive a **unified diff** patch to review in Git or apply locally.

## Core Tools (MCP)

1. **AnalyzeSolution(pathOrGit, branch?, include?, exclude?, severity?, maxDiagnostics?)**

    * *Input:* repo path or Git URL (read‑only clone), filters.
    * *Output:* solution summary, per‑project counts, top diagnostics with {project, file, line, id, severity, message}.

2. **ListDiagnostics(project, ids?, files?, max?)**

    * *Input:* project name or path; optional rule filters.
    * *Output:* diagnostics list + stats (by ID/severity), suggested fixable IDs.

3. **ApplyFixes(project, ids\[], previewOnly?)**

    * *Input:* rule IDs to fix (e.g., \["RCS1213","SA1101"]).
    * *Behavior:* creates a **temporary workspace**, applies available Roslyn code fixes, formats.
    * *Output:* `{ changedFiles[], patch, fixersApplied[], notes[] }` (patch is unified diff).

4. **CreatePatch(before, after)**

    * *Input:* two text blobs.
    * *Output:* unified diff (DiffPlex) for downstream tooling.

## NuGet Dependencies

* `Microsoft.CodeAnalysis.CSharp` (Roslyn)
* `Roslynator.Analyzers` **or** `StyleCop.Analyzers` (rules)
* `DiffPlex` (unified diffs)
* Optional: `Microsoft.Build.Locator` (load .sln reliably), `LibGit2Sharp` (read‑only clone when `pathOrGit` is a URL)

## Architecture (at a glance)

* **MCP boundary:** Validates tool inputs → spins up ephemeral workspace.
* **Analyzer host:** Loads solution via MSBuildLocator → runs analyzers → collates diagnostics.
* **Fix engine:** Maps rule IDs → available CodeFixProviders → applies fixes deterministically.
* **Diff stage:** Compares original vs fixed tree → generates unified diff with DiffPlex.
* **Return:** Never mutates user repo; returns a patch + metadata.

```
Client ⇄ MCP Tools  →  Analyzer Host (Roslyn) → Fix Engine → DiffPlex → Patch
```

## Security & Guardrails

* **Read‑only by default:** local copy or read‑only Git clone; no script execution.
* **Allowlist analyzers/fixers:** only vetted packages and rule IDs.
* **Resource caps:** per‑request time/CPU/file count limits; max diff size.
* **Path sandboxing:** block traversal outside workspace; redact secrets in diffs.
* **Determinism:** pinned package versions; no network during analysis.

## Example Flow

1. `AnalyzeSolution("https://github.com/org/app.git", branch:"main")`
2. Agent proposes: fix `RCS1213`, `SA1101`. You confirm.
3. `ApplyFixes(project:"App.Core", ids:["RCS1213","SA1101"], previewOnly:true)`
4. Server returns `patch` + `changedFiles`. You apply or open a PR.

## Example Schemas (JSON)

**AnalyzeSolution → Response (abridged)**

```json
{
  "solution": "App.sln",
  "projects": 12,
  "diagnosticSummary": {"error": 3, "warning": 148, "info": 22},
  "topDiagnostics": [
    {"project":"App.Core","file":"Services/UserSvc.cs","line":87,
     "id":"RCS1213","severity":"warning",
     "message":"Remove unused parameter 'token'"}
  ]
}
```

**ApplyFixes → Response (abridged)**

```json
{
  "fixersApplied": ["RCS1213","SA1101"],
  "changedFiles": ["Services/UserSvc.cs","Controllers/AuthController.cs"],
  "patch": "--- a/Services/UserSvc.cs\n+++ b/Services/UserSvc.cs\n@@ ...\n- public Task DoThing(string token)\n+ public Task DoThing()\n"
}
```

## Operational Notes

* **Language:** C# (solution & project files). VB/FS not targeted in v1.
* **Scale:** Use per‑project batching to handle large solutions; cap diagnostics.
* **CI usage:** Run as a service; agent requests a patch → CI validates → PR.

## Success Metrics

* Warnings reduced per run; mean time to review a patch; % patches merged without edits; build/test pass rate after fixes.

## Roadmap (short)

* Fix‑All (solution‑wide) with chunked diffs
* PR creation (Octokit) as optional, still returning patch for review
* Opt‑in formatting pass (dotnet‑format) as a final step

---

**Result:** A safe, repeatable way for AI to *propose* code improvements, while you stay in control via reviewable patches.
