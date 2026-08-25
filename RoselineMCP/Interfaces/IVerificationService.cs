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
    /// <param name="baseDirectory">
    /// The directory every reported <see cref="DiagnosticDetail.File"/> is relativized against —
    /// <see cref="LoadedProject.BaseDirectory"/> of the handle whose <c>resolvedPath</c> travels in
    /// the same response, so the two can be joined and land on the real file.
    /// <see langword="null"/> means "no path to hang off" (an in-memory workspace) and leaves the
    /// paths absolute — the same value <see cref="LoadedProject.BaseDirectory"/> itself yields there.
    /// <para>
    /// Deliberately <b>required</b>, for the reason the changed-project scope is deliberately
    /// <em>not</em> a parameter: a default would silently reinstate the re-derived anchor this
    /// parameter exists to remove, and a caller that forgot it would ship wrong paths with no
    /// signal. Without a default, forgetting is a compile error (#199).
    /// </para>
    /// </param>
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
        string? baseDirectory,
        int max = 20,
        CancellationToken cancellationToken = default);
}
