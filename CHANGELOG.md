# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Atypical-Consulting/RoselineMCP/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Atypical-Consulting/RoselineMCP/releases/tag/v1.0.0
