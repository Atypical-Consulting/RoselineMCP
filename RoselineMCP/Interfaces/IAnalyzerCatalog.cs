using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Catalog of the analyzer/code-fix assemblies bundled with RoselineMCP (the Roslynator
/// assemblies mirrored into the <c>analyzers/</c> folder next to RoselineMCP.dll at build time —
/// see RoselineMCP.csproj). Exposes both the instantiated C# <see cref="DiagnosticAnalyzer"/>s,
/// for running analyzer-driven diagnostics, and the raw assemblies, so
/// <see cref="ICodeFixProviderFactory"/> can scan them for code fix providers.
/// </summary>
public interface IAnalyzerCatalog
{
    /// <summary>
    /// Every C#-supporting <see cref="DiagnosticAnalyzer"/> instantiated from the bundled
    /// analyzer assemblies. Empty when the <c>analyzers/</c> folder is missing.
    /// </summary>
    ImmutableArray<DiagnosticAnalyzer> Analyzers { get; }

    /// <summary>
    /// The successfully loaded bundled assemblies (analyzers and code-fix providers alike),
    /// for callers that need to scan them for other extension types.
    /// </summary>
    IReadOnlyList<Assembly> Assemblies { get; }
}
