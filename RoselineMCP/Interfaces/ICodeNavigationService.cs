using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Read-only structural/semantic navigation over a C# project using Roslyn. Every method returns a
/// compact, structured result (symbols, signatures, locations, relationships) so an AI agent can
/// orient itself without reading whole source files — the primary token-saving surface of the server.
/// </summary>
public interface ICodeNavigationService
{
    /// <summary>
    /// Finds symbols by wildcard/substring name pattern, or returns a single file's structural
    /// outline when <paramref name="file"/> is supplied without a query.
    /// </summary>
    Task<SymbolSearchResponse> SearchSymbolsAsync(
        string? project,
        string? query,
        string? file,
        string[]? kinds,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns declaration metadata (kind, accessibility, modifiers, signature, base types,
    /// interfaces, XML docs, definition location) for a single symbol, optionally including its
    /// definition source.
    /// </summary>
    Task<SymbolInfoResponse> GetSymbolInfoAsync(
        string? project,
        string symbol,
        bool includeSource,
        CancellationToken cancellationToken = default);

    /// <summary>Finds every reference (use site) of a symbol across the solution.</summary>
    Task<ReferencesResponse> FindReferencesAsync(
        string? project,
        string symbol,
        bool includeDefinition,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds implementations of an interface (or interface member), overrides of a virtual/abstract
    /// member, or derived types of a class.
    /// </summary>
    Task<ImplementationsResponse> FindImplementationsAsync(
        string? project,
        string symbol,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>Builds a depth-bounded caller and/or callee graph for a method, with cycle detection.</summary>
    Task<CallGraphResponse> GetCallGraphAsync(
        string? project,
        string method,
        string direction,
        int depth,
        int max,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a type's base-class chain, implemented interfaces, and/or derived types (derived list capped at <paramref name="max"/>).</summary>
    Task<TypeHierarchyResponse> GetTypeHierarchyAsync(
        string? project,
        string type,
        string direction,
        int max,
        CancellationToken cancellationToken = default);
}
