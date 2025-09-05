using Microsoft.CodeAnalysis.CodeFixes;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Factory for creating and managing code fix providers.
/// </summary>
public interface ICodeFixProviderFactory
{
    /// <summary>
    /// Gets a code fix provider for the specified diagnostic ID.
    /// </summary>
    /// <param name="diagnosticId">The diagnostic ID to get a provider for.</param>
    /// <returns>A code fix provider if available, null otherwise.</returns>
    CodeFixProvider? GetProviderForDiagnostic(string diagnosticId);

    /// <summary>
    /// Gets all available diagnostic IDs that have code fix providers.
    /// </summary>
    /// <returns>Collection of fixable diagnostic IDs.</returns>
    IEnumerable<string> GetFixableDiagnosticIds();

    /// <summary>
    /// Loads all available code fix providers.
    /// </summary>
    void LoadProviders();
}