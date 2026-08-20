using Microsoft.CodeAnalysis;
using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Compiles a candidate <see cref="Solution"/> in memory and reports what that change did to the
/// compiler's verdict. This is the machinery behind the write tools' refusal gate and behind
/// <c>check_compilation</c> — the sub-second replacement for a <c>dotnet build</c> round trip.
/// </summary>
/// <remarks>
/// Compiler diagnostics only, deliberately: analyzers cost several times a bare compile and would
/// turn a build gate into a style gate (<c>list_diagnostics</c> already covers analyzers).
/// </remarks>
public interface IVerificationService
{
    /// <summary>
    /// Verifies <paramref name="candidate"/>, optionally against <paramref name="baseline"/>.
    /// </summary>
    /// <param name="baseline">
    /// The before-state to compare against, or <see langword="null"/> for an absolute verdict
    /// (<c>check_compilation</c>), which populates <see cref="VerificationVerdict.Errors"/> instead
    /// of the delta.
    /// </param>
    /// <param name="candidate">The solution to compile — for a write tool, the in-memory result of
    /// the edit, which has not touched disk.</param>
    /// <param name="max">
    /// Maximum number of diagnostics to report in any one list; the rest are counted in
    /// <see cref="VerificationVerdict.Omitted"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <remarks>
    /// The changed-project set is derived internally from <c>candidate.GetChanges(baseline)</c>
    /// rather than accepted as a parameter: a caller that under-reports it would silently narrow the
    /// scope and let the gate pass broken code.
    /// </remarks>
    Task<VerificationVerdict> VerifyAsync(
        Solution? baseline,
        Solution candidate,
        int max = 20,
        CancellationToken cancellationToken = default);
}
