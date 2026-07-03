# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security
- Pinned `Microsoft.Bcl.Memory` to `9.0.14` in `RoselineMCP.TokenBenchmark` to override the `9.0.4`
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
