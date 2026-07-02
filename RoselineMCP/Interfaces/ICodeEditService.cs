using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Surgical, symbol-aware edits over a C# project using Roslyn. Each operation changes only the
/// targeted member (or its references, for a rename) and returns a unified diff, so the tokens an
/// agent must emit to change code stay proportional to the change rather than the file size. All
/// operations default to preview mode and never write to disk unless the caller explicitly opts in.
/// </summary>
public interface ICodeEditService
{
    /// <summary>
    /// Replaces, adds, or deletes a single type member. For <c>replace</c>/<c>delete</c>,
    /// <paramref name="symbol"/> is the member; for <c>add</c>, it is the container type and
    /// <paramref name="newSource"/> is the member declaration to insert.
    /// </summary>
    Task<EditMemberResponse> EditMemberAsync(
        string project,
        string symbol,
        string operation,
        string? newSource,
        bool previewOnly,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a symbol and updates every reference across the solution using Roslyn's rename engine.
    /// </summary>
    Task<RenameSymbolResponse> RenameSymbolAsync(
        string project,
        string symbol,
        string newName,
        bool previewOnly,
        CancellationToken cancellationToken = default);
}
