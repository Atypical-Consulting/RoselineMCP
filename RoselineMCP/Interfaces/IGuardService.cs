using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Answers "did the write that just happened to this file break the build?" — the engine behind the
/// compile guard, which runs after <b>every</b> file write regardless of which tool made it.
/// </summary>
/// <remarks>
/// <para>
/// It reports a <em>delta</em>, never an absolute verdict: only errors this edit introduced are
/// worth an agent's attention. Errors that were already there belong to the branch, not to the
/// agent, and reporting them is how a guard turns into a degradation loop.
/// </para>
/// <para>
/// The delta is only meaningful across solutions that share a Roslyn lineage, so the service keeps
/// its own <c>Solution</c> snapshot per resolved path and edits it forward from disk rather than
/// reloading. Measured on two independent loads of the same already-broken solution, a reload-based
/// baseline reports <c>introduced: 1, preexisting: 0</c> — every pre-existing error blamed on the
/// agent — because a fresh load mints fresh <c>ProjectId</c>s and <c>DocumentId</c>s and
/// <c>GetChanges</c> then has nothing to match against.
/// </para>
/// </remarks>
public interface IGuardService
{
    /// <summary>
    /// Verifies the solution owning <paramref name="absoluteFilePath"/> against the guard's previous
    /// view of it.
    /// </summary>
    /// <param name="absoluteFilePath">Absolute path of the file that was just written.</param>
    /// <param name="cancellationToken">Token used to cancel the verification.</param>
    /// <returns>
    /// A <see cref="GuardReport"/>, silent unless this edit introduced compiler errors. Never throws
    /// for an ordinary "nothing to check" situation — a file outside any project is a silent report,
    /// not a failure.
    /// </returns>
    Task<GuardReport> VerifyFileAsync(string absoluteFilePath, CancellationToken cancellationToken = default);
}
