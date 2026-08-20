using ModelContextProtocol;
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
    /// <param name="project">Project name, directory, <c>.csproj</c> or <c>.sln</c> path; auto-discovered when null.</param>
    /// <param name="symbol">The member to edit, or the container type for <c>add</c>.</param>
    /// <param name="operation"><c>replace</c>, <c>add</c>, or <c>delete</c>.</param>
    /// <param name="newSource">The member declaration for <c>replace</c>/<c>add</c>.</param>
    /// <param name="previewOnly">When true (the default at the tool boundary), nothing is written.</param>
    /// <param name="allowIntroducedErrors">
    /// Escape hatch for the compile gate. When false (the default) an edit whose candidate
    /// compilation introduces compiler errors is <b>refused</b>: the response carries the diff and
    /// the introduced errors, <c>applied</c> is false, and no file is written.
    /// </param>
    /// <param name="max">Maximum diagnostics reported per list in the verdict; the rest are counted.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<EditMemberResponse> EditMemberAsync(
        string? project,
        string symbol,
        string operation,
        string? newSource,
        bool previewOnly,
        bool allowIntroducedErrors = false,
        int max = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a symbol and updates every reference across the solution using Roslyn's rename engine.
    /// </summary>
    /// <param name="project">Project name, directory, <c>.csproj</c> or <c>.sln</c> path; auto-discovered when null.</param>
    /// <param name="symbol">The symbol to rename.</param>
    /// <param name="newName">The new identifier.</param>
    /// <param name="previewOnly">When true (the default at the tool boundary), nothing is written.</param>
    /// <param name="allowIntroducedErrors">See <see cref="EditMemberAsync"/> — the same compile gate.</param>
    /// <param name="max">Maximum diagnostics reported per list in the verdict; the rest are counted.</param>
    /// <param name="progress">Optional progress sink for the load/resolve/rename phases.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<RenameSymbolResponse> RenameSymbolAsync(
        string? project,
        string symbol,
        string newName,
        bool previewOnly,
        bool allowIntroducedErrors = false,
        int max = 20,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default);
}
