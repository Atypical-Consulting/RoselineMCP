using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Factory for creating and managing code fix providers.
/// </summary>
/// <remarks>
/// Providers come from two layers. The <b>process-wide map</b> — the Roslyn built-ins and the
/// bundled Roslynator catalog — is built once and is what the no-<see cref="Project"/> members
/// answer from. The <b>project overlay</b> adds the providers carried by a target project's own
/// <see cref="Project.AnalyzerReferences"/>, the assemblies whose analyzers the diagnostics pass
/// already runs; it is consulted only after the process-wide map, so an ID both can fix resolves
/// to the bundled provider and nothing already fixable changes behaviour.
/// </remarks>
public interface ICodeFixProviderFactory
{
    /// <summary>
    /// Gets a code fix provider for the specified diagnostic ID from the process-wide map.
    /// Equivalent to <see cref="GetProviderForDiagnostic(string, Project?)"/> with a
    /// <see langword="null"/> project.
    /// </summary>
    /// <param name="diagnosticId">The diagnostic ID to get a provider for.</param>
    /// <returns>A code fix provider if available, null otherwise.</returns>
    CodeFixProvider? GetProviderForDiagnostic(string diagnosticId);

    /// <summary>
    /// Gets a code fix provider for the specified diagnostic ID — from the process-wide map first,
    /// then from the providers carried by <paramref name="project"/>'s own analyzer references.
    /// </summary>
    /// <param name="diagnosticId">The diagnostic ID to get a provider for.</param>
    /// <param name="project">
    /// The target project whose <see cref="Project.AnalyzerReferences"/> are searched after the
    /// process-wide map; <see langword="null"/> searches the process-wide map only.
    /// </param>
    /// <returns>A code fix provider if available, null otherwise.</returns>
    CodeFixProvider? GetProviderForDiagnostic(string diagnosticId, Project? project);

    /// <summary>
    /// Gets all diagnostic IDs that have a code fix provider in the process-wide map. Equivalent
    /// to <see cref="GetFixableDiagnosticIds(Project?)"/> with a <see langword="null"/> project.
    /// </summary>
    /// <returns>Collection of fixable diagnostic IDs.</returns>
    IEnumerable<string> GetFixableDiagnosticIds();

    /// <summary>
    /// Gets all diagnostic IDs that have a code fix provider: the process-wide map plus the
    /// providers carried by <paramref name="project"/>'s own analyzer references.
    /// </summary>
    /// <param name="project">
    /// The target project whose <see cref="Project.AnalyzerReferences"/> contribute their
    /// providers; <see langword="null"/> yields the process-wide map only.
    /// </param>
    /// <returns>Collection of fixable diagnostic IDs.</returns>
    IEnumerable<string> GetFixableDiagnosticIds(Project? project);

    /// <summary>
    /// Loads all available code fix providers.
    /// </summary>
    void LoadProviders();
}
