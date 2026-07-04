# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **`analyze_solution` reports honest numbers.** `diagnosticSummary` now counts every diagnostic
  passing the filters — previously each project's diagnostics were capped at `maxDiagnostics`
  *before* counting, undercounting any project with more. `topDiagnostics` is now the true
  solution-wide top-N by severity — previously it kept the first N diagnostics encountered in
  project order, so warnings from an early project could crowd out errors from a later one.
- Configuration (`appsettings.json` / `appsettings.{Environment}.json`) now loads from the install
  directory (`AppContext.BaseDirectory`) instead of the process working directory — a target
  repository's own `appsettings.json` can no longer reconfigure the server, the settings packaged
  with the dotnet tool are actually found, and the needless reload-on-change file watchers are
  gone. Removed the dead `RoselineMCP:MaxDiagnostics` key from `appsettings.json`.
- MSBuild registration now picks the newest installed SDK instead of whatever
  `MSBuildLocator` enumerates first, and `CreateWorkspace` fails fast with an actionable error
  when no MSBuild/.NET SDK instance could be registered (instead of surfacing a confusing
  workspace load failure later).
- Whitespace-only changes are no longer silently dropped from diffs (the diff engine ignored
  whitespace unconditionally): a whitespace-only `edit_member` no longer reports "No changes were
  produced" and skips the write even with `previewOnly: false`, `apply_fixes` patches no longer
  omit whitespace-only changes that were written to disk, and `create_patch`'s `ignoreWhitespace`
  parameter now actually controls the behavior (default `false`); `create_patch` line counts also
  no longer miss content lines that themselves start with `++`/`--`.
- **Docs `/releases` page could miss the just-published release.** The page is generated from the
  GitHub Releases *listing* API at build time and is rebuilt immediately after the publish workflow,
  but that listing endpoint can trail `/releases/latest` by a few minutes — so a new release could be
  absent from the page until a manual re-deploy (as happened for v2.0.0). The build now cross-checks
  `/releases/latest`, retries the listing until it includes that tag (bounded so the build never
  hangs), and merges the latest release in directly as a fallback.

### Performance
- **`analyze_solution` analyzes projects in parallel** (bounded by the processor count) instead of
  one at a time; results are merged deterministically and progress values still strictly increase.

## [2.0.0] - 2026-07-04

### Added
- **Server-level tool guidance to drive adoption.** The server now sends MCP `instructions` — a
  decision policy telling the model to prefer these structural tools over reading whole files
  (especially on large codebases) — and each read-only tool's description is rewritten as a decision
  rule ("prefer over Read/Grep to answer 'where is this used'") rather than a feature list. In
  end-to-end testing this flipped the agent from never calling the tools to using them unprompted on
  large solutions. See [`docs/AGENT-BENCHMARK.md`](docs/AGENT-BENCHMARK.md#follow-up--making-the-model-actually-use-the-tools).
- **`project` is now optional on the Roslyn-backed tools** (`search_symbols`, `get_symbol_info`,
  `find_references`, `find_implementations`, `get_call_graph`, `get_type_hierarchy`, `edit_member`,
  `rename_symbol`) — when omitted it is auto-discovered from the working directory (searching the
  cwd, a few parent directories, and immediate subdirectories) — and a `.sln` path is now accepted
  wherever `project` is passed. Reduces the friction that made agents fail calls guessing the
  project (they naturally tried the `.sln`, which used to fail).

### Changed
- **BREAKING: leaner response shapes for the read-only navigation tools and `get_symbol_info`** — a
  token-efficiency pass trimmed redundant and always-present fields from the JSON these tools
  return (tool names and input parameters are unchanged). Concretely:
  - **Relative file paths.** Every `file`/`definitionFile` is now solution-root-relative with
    forward slashes (e.g. `RoselineMCP/Services/Foo.cs`) instead of an absolute path — across
    `search_symbols`, `get_symbol_info`, `find_references`, `find_implementations`,
    `get_call_graph`, and `get_type_hierarchy`.
  - **`truncated` is omitted when `false`.** Its absence now means "not truncated" — for
    `search_symbols`, `find_references`, `find_implementations`, every `get_call_graph` node, and
    `get_type_hierarchy`'s `derivedTypesTruncated`.
  - **`find_references`** drops the `column` field from each reference (now just `file`, `line`,
    `snippet`).
  - **`get_call_graph`** drops each node's `signature`; the node `fullName` now renders parameter
    **types as simple names** (e.g. `RoselineMCP.Services.Foo.Bar(string, CancellationToken)`),
    still parameter-qualified so overloads stay distinct — call `get_symbol_info` for a method's
    full signature.
  - **Redundant fields dropped from symbol summaries and `get_symbol_info`.** `accessibility` is
    gone (it is already inside `signature`) from `get_symbol_info` and the project-wide summaries;
    `containingType` is gone from the full summaries (it is already the prefix of `fullName`). The
    single-file outline of `search_symbols` still emits `containingType`, but now as the *simple*,
    unqualified type name.
  - **`get_symbol_info`** now omits `modifiers`, `baseTypes`, `interfaces`, `documentation`, and
    `source` when they are empty/absent, so a minimal symbol collapses to `name`, `fullName`,
    `kind`, and `signature`.

  Net effect: tool output is ~35% smaller, lifting the benchmark headline savings from a pooled
  **81% to 88%** (median per task 76% → 85%) on RoselineMCP's own source. These are breaking
  changes to the read-only tools' response wire shapes; update any client that parsed the removed
  fields or relied on absolute paths.
- `deploy-docs.yml` retries the GitHub Pages deploy up to 3× — it intermittently returns
  "Deployment failed, try again later" (a Pages backend hiccup, not a build failure) that clears on
  re-run. The first two attempts tolerate failure, so a transient miss no longer fails the job.

### Documentation
- **End-to-end agent benchmark** ([`docs/AGENT-BENCHMARK.md`](docs/AGENT-BENCHMARK.md)) — a controlled
  A/B (vanilla Claude Code vs. + RoselineMCP, same task, same model, quality-gated) measuring whether
  an agent actually consumes fewer tokens in practice. Finding: ~50% fewer tokens at equal quality on
  large-file codebases, break-even on tiny repos, and the model must be steered to use the tools.
- **Tools page aligned to the v1.4.0 contract.** Added a response-envelope callout
  (`{ ok, data }` / `{ ok, error }`, `structuredContent`/`outputSchema`), surfaced each tool's
  human `Title` and capability pills (`progress`, `confirms`/elicitation), gave every tool an anchor
  link, and dropped the stale `new` badges (those tools shipped in 1.3.0). The page had been showing
  the pre-1.4.0 flat response shape.
- Docs site: added a **GitHub "Star" button** in the top bar showing the star count. Renders a
  build-time snapshot instantly, then a tiny client-side fetch refreshes it to the current count
  (falls back to the build-time value on rate-limit/error).

## [1.4.0] - 2026-07-03

### Added
- **MCP structured content + output schema** — every tool now advertises an `outputSchema` and
  emits `structuredContent` (`UseStructuredContent = true`), so clients get a machine-readable,
  schema-validated result in addition to the JSON text.
- **Progress notifications** for long-running tools — `AnalyzeSolution`, `ApplyFixes`, and
  `RenameSymbol` now report progress via MCP progress notifications.
- **Human-readable tool titles and an honest `OpenWorld` hint** — every tool advertises a `Title`;
  `OpenWorld` is `false` for the local-only tools and `true` only for `AnalyzeSolution` (which can
  clone a Git URL).
- **Write confirmation via MCP elicitation** — the write tools (`ApplyFixes`, `EditMember`,
  `RenameSymbol`) ask the client to confirm before writing when `previewOnly: false`; declining
  downgrades the call to a preview (nothing is written).
- **Failure logging via MCP logging notifications** — tool failures are surfaced to the client's
  log stream through MCP logging notifications that carry the correlation ID.

### Changed
- **BREAKING: tools now return a typed `ToolResult<T>` envelope** instead of a hand-serialized JSON
  string. The response wire shape changed: success payloads are now nested under `data`
  (`{ "ok": true, "data": { ... } }`) and failures under `error`
  (`{ "ok": false, "error": { "type", "message", "hint?", "correlationId" } }`). The human-readable
  message moved from the top-level `error` field to `error.message`; `type`/`hint`/`correlationId`
  moved under `error`. See [`docs/API.md`](docs/API.md#response-envelope).
- `server.json` `websiteUrl` now points at the documentation site
  (`https://atypical-consulting.github.io/RoselineMCP/`) instead of the GitHub repo. Reaches the
  live MCP Registry entry on the next published version.

## [1.3.3] - 2026-07-03

### Fixed
- **MCP Registry publish 403'd on namespace casing.** The registry namespace derived from GitHub
  OIDC is case-sensitive and matches the org's canonical login (`Atypical-Consulting`), but
  `server.json`'s `name` and the README ownership marker used lowercase
  (`io.github.atypical-consulting/...`), so the first publish was `Forbidden`. Corrected both to
  `io.github.Atypical-Consulting/roseline-mcp`. (The lowercase GHCR image name is unrelated and
  stays lowercase, as Docker requires.)
- **Docs `/releases` page didn't refresh on new releases.** The `release:` trigger on
  `deploy-docs.yml` never fired because the release is created by the publish workflow's
  `GITHUB_TOKEN`, and GitHub does not start workflows from token-created events. Switched to a
  `workflow_run` trigger on the `Publish NuGet` workflow's completion, which keys off the workflow
  (whose original trigger was a human tag push) and fires regardless of the publish outcome.

## [1.3.2] - 2026-07-03

### Documentation
- **Docs site: a Releases page generated from GitHub Releases at build time** (notes rendered from
  each release, plus direct `.mcpb`/`.nupkg` download buttons), a new *Claude Desktop (1-click)*
  install tab, and a note that RoselineMCP is listed in the official MCP Registry. The Astro build
  fetches the Releases API (authenticated in CI to avoid rate limits) and degrades to a GitHub
  link-out if the fetch fails.

### Added
- **One-click install for Claude Desktop (MCPB bundle).** A `mcpb/manifest.json` (MCPB spec 0.3)
  describes RoselineMCP as a `dnx`-launched server; the release now builds and attaches a
  `RoselineMCP.mcpb` to each GitHub Release, so users can install with a dialog instead of editing
  JSON config. The bundle only wraps the `dnx RoselineMCP` launch (the .NET 10 SDK is still
  required, since analysis loads projects through MSBuild), so it stays tiny and platform-agnostic.
- **Automated MCP Registry publishing.** `publish-nuget.yml` now has a `publish-registry` job that,
  after a successful NuGet publish, waits for the version to index, then authenticates via GitHub
  OIDC (`mcp-publisher login github-oidc`, no secret) and publishes `.mcp/server.json` to the
  official registry (`registry.modelcontextprotocol.io`) — so the server is discoverable by any
  client/aggregator that reads the registry. Ownership is proven by an `mcp-name:` marker added to
  the packed `README.md`, which the registry cross-checks against the NuGet package. The manifest
  `$schema` was migrated from the deprecated `2025-10-17` to the current `2025-12-11` (a URL-only
  change; the format is unchanged for stdio package servers). Takes effect on the next tagged release.

### Security
- Pinned `Microsoft.Bcl.Memory` to `10.0.9` (aligned with the `net10.0` TFM) in
  `RoselineMCP.TokenBenchmark` to override the `9.0.4`
  that `Microsoft.ML.Tokenizers` `2.0.0` pulled in transitively, which was vulnerable to
  CVE-2026-26127 (GHSA-73j8-2gch-69rq, high severity — Base64Url out-of-bounds-read DoS). The
  benchmark harness is never packaged and is not referenced by the shipped `RoselineMCP` package,
  so published users were never exposed; this clears the `NU1903` restore warning. Remove the pin
  once `Microsoft.ML.Tokenizers` references a patched build.

## [1.3.1] - 2026-07-03

### Changed
- **`publish-nuget.yml` now creates a GitHub Release and verifies the artifact before publishing.**
  A git tag is not a GitHub Release, and nothing was creating one — so tagged versions published to
  NuGet without a corresponding Release (`v1.3.0` had to be backfilled by hand). The release job now:
  (1) fails the build if the packed `.nupkg` is missing `.mcp/server.json` or its embedded version
  doesn't match the tag — a guard that would have caught the original `dnx`-fetches-`1.0.0` bug at
  the source rather than in the wild; and (2) after a successful NuGet push, creates (or heals) the
  matching GitHub Release with notes extracted from this CHANGELOG and the `.nupkg` attached.

### Dependencies
- `Microsoft.ML.Tokenizers` and `Microsoft.ML.Tokenizers.Data.Cl100kBase` `1.0.3` → `2.0.0`
  (`RoselineMCP.TokenBenchmark` only — not part of the shipped package) (#76)
- Website: `astro` `5.x` → `7.0.0` (#77) and CI Node `20` → `24` (#75)
- `actions/upload-pages-artifact` action `4` → `5` (#74)

## [1.3.0] - 2026-07-03

### Added
- **Token-efficient code navigation tools** — six new read-only MCP tools that let an AI agent
  retrieve precise structural/semantic information via Roslyn instead of reading whole files
  (source code typically dominates an agent's token budget):
  - `search_symbols` — find symbols by wildcard/substring name pattern, or outline a single file
  - `get_symbol_info` — a symbol's kind, accessibility, modifiers, signature, base types,
    interfaces, XML docs, and definition location (optionally its source) — the compact
    "go to definition" payload
  - `find_references` — every use site of a symbol across the solution, as location + snippet
  - `find_implementations` — implementations of an interface/member, overrides, or derived types
  - `get_call_graph` — a depth-bounded caller/callee graph with cycle detection
  - `get_type_hierarchy` — a type's base-class chain, interfaces, and derived types
- **Surgical code-editing tools** — two new write tools that emit a member-level change (not a
  whole-file rewrite), keeping the tokens an agent produces proportional to the change. Both
  default to preview mode (`previewOnly: true`) like `ApplyFixes`, so nothing is written to disk
  unless the caller passes `previewOnly: false` explicitly:
  - `edit_member` — replace, add, or delete a single type member
  - `rename_symbol` — rename a symbol and update every reference across the solution (Roslyn rename)
- `IProjectLoader`/`ProjectLoader` service that loads a project — and its containing solution when
  present, so references and renames span projects — into a fresh workspace per call, plus
  `ICodeNavigationService` and `ICodeEditService` and their response models.
- **Token-savings benchmark** (`RoselineMCP.TokenBenchmark`) — a reproducible harness that runs the
  real services against RoselineMCP's own source and measures each tool's output against the source
  an agent would otherwise read, tokenized with cl100k_base. Systematic sweeps; results stamped with
  commit + date. Reproduce with `dotnet run --project RoselineMCP.TokenBenchmark -c Release`.
- **Documentation site** (`website/`, Astro) with an overview, the tool reference, and the honest
  benchmark (charts + methodology + limitations), deployed to GitHub Pages via
  `.github/workflows/deploy-docs.yml`. Across 477 navigation tasks the read-only tools showed a
  pooled 81% / median 74% token reduction versus reading the corresponding files.

### Changed
- **`search_symbols` file outline is now token-lean.** The benchmark caught the outline *costing*
  tokens (it repeated the file path and fully-qualified name on every symbol); it now returns a
  lean projection (name, kind, signature, line), flipping its median from −45% to +30%.
  `SymbolSummary` also omits null fields from its JSON. Project-wide search is unchanged.

### Fixed
- **`dnx`-based installs pulled an ancient `1.0.0`.** `.mcp/server.json` hardcoded `version`/
  `packages[0].version` at `1.0.0` — a version that was never released (releases start at `1.2.0`)
  — so any client resolving the MCP manifest was told to fetch `1.0.0`. Worse, despite
  `PackageType=McpServer` the manifest was never packed into the `.nupkg` (it lives at the repo
  root with no `<None Include>` wiring it in), so the McpServer package shipped without its own
  manifest. Fixed by: (1) correcting the manifest to the current release, (2) packing
  `../.mcp/server.json` into the package at `.mcp/server.json`, and (3) stamping the version into
  the manifest from the release tag in `publish-nuget.yml` (mirroring `MinVerVersionOverride`) so
  it can never drift out of lockstep with the package version again.

## [1.2.1] - 2026-07-02

### Fixed
- `publish-nuget.yml`'s `Pack` and `Push to NuGet.org` steps used multi-line `run:` blocks
  without the YAML block-literal indicator (`run: |`). YAML folds unmarked multi-line scalars'
  line breaks into spaces, and bash then treats each resulting `\<space>` as an escaped space
  rather than a line continuation — collapsing the entire multi-line command into a single
  garbled argument and failing with `MSB1008: Only one project can be specified`. This was the
  very first execution of this workflow (never run before `v1.2.0`) and had never been caught.
  `v1.2.0`'s Docker images published successfully to Docker Hub and GHCR; its NuGet package did
  not, hence this immediate `1.2.1` follow-up to actually publish to NuGet.org.

## [1.2.0] - 2026-07-02

### Added
- `SECURITY.md`, `LICENSE` (MIT), and `CODE_OF_CONDUCT.md` — the repository previously had no
  formal security-reporting policy or license file
- `.mcp/server.json` MCP registry manifest, enabling `dnx`-based installs (no `dotnet tool
  install` step required) for clients that support it
- Real, read-only Git URL support for `AnalyzeSolution`'s `pathOrGit` (shallow `git clone --depth
  1` of `http(s)://` URLs into an auto-deleted temp directory), plus a bounded clone timeout
- Explicit MCP tool annotations (`readOnlyHint`/`destructiveHint`/`idempotentHint`) on every tool
- A closed, stable machine-readable error `type` contract (`ValidationError`, `NotFoundError`,
  `AnalysisError`, `CancelledError`, `TimeoutError`, `InternalError`) shared by every tool, plus a
  configurable per-call wall-clock timeout (`RoselineMCP:DefaultTimeout`)
- `.github/workflows/codeql.yml` for static security analysis, plus PR/issue templates

### Changed
- **Security default**: `ApplyFixes`' `previewOnly` now defaults to `true` at the MCP tool
  boundary, so calling it without setting the parameter never writes to disk
- `IsFixableDiagnostic`/`suggestedFixableIds` are now derived from whichever code fix providers
  actually loaded at runtime (`ICodeFixProviderFactory`), instead of a hand-maintained static list
  that could drift out of sync
- Updated `ModelContextProtocol` SDK to `1.4.0`
- Updated `Microsoft.Extensions.Hosting` to `10.0.9`
- Updated `Microsoft.CodeAnalysis.*` to `5.3.0` (Roslyn)
- Updated `Roslynator.*` to `4.15.0`
- Updated MSBuild packages to `18.7.1`
- Updated `FakeItEasy` to `9.0.1`
- Updated `Microsoft.NET.Test.Sdk` to `18.7.0`
- Updated `coverlet.msbuild` and `coverlet.collector` to `10.0.1`
- Updated GitHub Actions: `checkout` to v7, `setup-dotnet` to v5, `upload-artifact` to v7, Docker
  actions to latest
- CI: fixed the coverage-report file lookup to target the `coverlet.msbuild`-generated
  `coverage.cobertura.xml` specifically, avoiding a mismatch with GUID-named reports from
  `Microsoft.Testing.Extensions.CodeCoverage`
- Fixed the NuGet packaging condition so `RuntimeIdentifiers` no longer applies to `dotnet pack`
  (it previously risked restricting the `PackAsTool` package to RID-specific assets, which would
  break `dotnet tool install` on platforms other than the one it was packed on)

### Documentation
- Full accuracy pass across `README.md`, `CLAUDE.md`, `CONTRIBUTING.md`, `PUBLISH.md`,
  `docs/API.md`, and `docs/ARCHITECTURE.md`: corrected tool parameter names and response shapes to
  match current source, replaced inaccurate "regex"/"glob" filter descriptions with the actual
  substring-match behavior, reconciled `Security` sections with `SECURITY.md`'s MSBuild
  code-execution caveat, rewrote `PUBLISH.md` to describe the actual tag-triggered CI/CD publish
  flow (removing three obsolete manual publish methods), fixed `.NET 9.0`/upstream-remote
  references in `CONTRIBUTING.md`, and added Tool Annotations / Tool Compatibility Policy / MCP
  Client Compatibility sections
- Fixed stale Tech Stack table in README (versions now match actual csproj)
- Fixed CLAUDE.md runtime reference (.NET 9.0 → .NET 10.0, MCP SDK version)
- Removed broken RepoBeats analytics placeholder and a dangling empty "Stats" heading
- Added architecture note clarifying stdio transport as intentional design decision

## [1.1.0] - 2026-02-26

### Added
- README badges for CI, NuGet, Docker, and License
- CHANGELOG.md to track version history
- "Getting Started with Claude Desktop" section in README for all installation methods (NuGet global tool, Docker, build from source)

### Changed
- Upgraded to .NET 10.0 (from .NET 9.0)
- Updated `ModelContextProtocol` SDK to `0.9.0-preview.2` (from `0.3.0-preview.4`)
- Updated `Microsoft.Extensions.Hosting` to `10.0.1` (from `9.0.2`)
- Updated documentation to reflect .NET 10.0 requirement
- All 213 tests passing on .NET 10.0

### Notes
- `ModelContextProtocol` SDK 1.0.0 (stable) is now available on NuGet — upgrade is tracked for next release

## [1.0.0] - (Initial Release)

### Added
- Comprehensive C# solution analysis using Roslyn
- Automated code fix application for hundreds of diagnostic rules
- Support for Roslynator, StyleCop, and custom analyzers
- Unified diff generation for preview before applying changes
- Flexible filtering by severity, diagnostic ID, file patterns, and projects
- MCP protocol integration for AI assistant usage
- NuGet global tool packaging
- Docker container support with Alpine base image
- Full documentation and examples
- Unit tests

### Features
- **AnalyzeSolution**: Analyze entire C# solutions
- **ListDiagnostics**: Get detailed diagnostics for specific projects
- **ApplyFixes**: Apply automated code fixes
- **CreatePatch**: Generate unified diffs

[Unreleased]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.2.1...HEAD
[1.2.1]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Atypical-Consulting/RoselineMCP/releases/tag/v1.0.0
