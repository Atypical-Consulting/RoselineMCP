namespace RoselineMCP.Models;

/// <summary>
/// What the compile guard has to say about one file write — usually nothing.
/// </summary>
/// <remarks>
/// <see cref="Silent"/> is the normal outcome and carries no payload: the guard fires after every
/// write, so anything it emits on the ordinary path would be pure cost. A report is
/// <see cref="Silent"/> when the file belongs to no project, when this is the first time the guard
/// has seen the solution (nothing to compare against yet), when the structure changed and the
/// baseline had to be re-established, or when the edit simply introduced no compiler errors —
/// including on a repository that was already red.
/// </remarks>
public sealed class GuardReport
{
    /// <summary>Whether the guard has nothing to say. When <see langword="true"/>, say nothing at all.</summary>
    public bool Silent { get; private init; }

    /// <summary>
    /// The rendered text to hand back, or <see langword="null"/> when <see cref="Silent"/>. Produced
    /// once, here, by <c>GuardReportFormatter</c> — the flag and the text can never disagree because
    /// the flag is derived from the text.
    /// </summary>
    public string? Text { get; private init; }

    /// <summary>
    /// Absolute path of the <c>.sln</c> (or <c>.csproj</c>) the verdict is about, when one was
    /// resolved. The guard anchors on the edited file, so this names the checkout that was actually
    /// verified — which is the only thing distinguishing a git worktree from its main checkout.
    /// </summary>
    public string? ResolvedPath { get; private init; }

    /// <summary>The underlying verdict, when the guard spoke; <see langword="null"/> otherwise.</summary>
    public VerificationVerdict? Verdict { get; private init; }

    /// <summary>A report with nothing to say.</summary>
    public static GuardReport Quiet(string? resolvedPath = null) =>
        new() { Silent = true, ResolvedPath = resolvedPath };

    /// <summary>A report carrying introduced compiler errors.</summary>
    public static GuardReport Speaking(VerificationVerdict verdict, string text, string? resolvedPath) =>
        new() { Silent = false, Verdict = verdict, Text = text, ResolvedPath = resolvedPath };
}
