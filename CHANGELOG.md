# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- README badges for CI, NuGet, Docker, and License
- CHANGELOG.md to track version history

### Changed
- Upgraded to .NET 10.0
- Updated ModelContextProtocol to 0.9.0-preview.2 (from 0.3.0-preview.4)
- Updated Microsoft.Extensions.Hosting to 10.0.1 (from 9.0.2)
- Updated documentation to reflect .NET 10.0 requirement

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

[Unreleased]: https://github.com/phmatray/RoselineMCP/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/phmatray/RoselineMCP/releases/tag/v1.0.0
