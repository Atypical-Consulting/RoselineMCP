# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Atypical-Consulting/RoselineMCP/releases/tag/v1.0.0
