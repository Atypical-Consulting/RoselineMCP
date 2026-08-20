using System.Text;

namespace RoselineMCP.Models;

/// <summary>
/// What a confirmed write actually reaches — the closed vocabulary the write-confirmation prompt
/// describes its scope with.
/// </summary>
/// <remarks>
/// <para>
/// It is an enum rather than three sentences because three sentences is what the tools had, and they
/// diverged: a <em>conditional</em> qualifier in <c>apply_fixes</c>, an <em>unconditional</em> clause
/// branching on the operation in <c>edit_member</c>, and <em>none</em> in <c>rename_symbol</c>, each
/// with its own correctness argument in a comment above its own lambda. #129 centralised the gate's
/// policy and deliberately left the wording per-tool; in the four PRs that followed, the wording
/// drifted exactly where the policy no longer could (#161b).
/// </para>
/// <para>
/// The three members are not interchangeable and the differences are load-bearing — see
/// <see cref="WritePrompt.Render"/>, which holds the one rendering of each and the reasoning behind
/// it. A fourth write tool has to pick one, which is the point: it inherits wording its siblings
/// already agreed to instead of authoring a fourth phrasing.
/// </para>
/// </remarks>
public enum WriteScope
{
    /// <summary>
    /// One project's documents — the anchor project a solution is narrowed to
    /// (<c>ProjectLoader.SelectPrimaryProject</c>), or the target project itself. <c>apply_fixes</c>.
    /// </summary>
    PrimaryProjectOf,

    /// <summary>
    /// Exactly one file: the declaration the symbol resolves to, which may sit in a project the
    /// caller never named. <c>edit_member</c>.
    /// </summary>
    SingleFile,

    /// <summary>
    /// Every project in the solution. <c>rename_symbol</c> — <c>Renamer.RenameSymbolAsync</c>
    /// really is solution-wide.
    /// </summary>
    WholeSolution,
}

/// <summary>
/// The structured inputs a write-confirmation sentence is rendered from: a <see cref="WriteScope"/>
/// and the values that scope names.
/// </summary>
/// <remarks>
/// <para>
/// This type exists so the gate receives <em>values</em> rather than a finished sentence. The three
/// tools used to hand <c>ResolveWriteModeAsync</c> a <c>Func&lt;string, string&gt;</c> that had
/// already interpolated the caller's <c>symbol</c> and <c>newName</c> — by the time the helper saw
/// the string, the injection had happened and there was nothing left to escape (#161a). Passing the
/// values instead is what makes the escaping structural: <see cref="Render"/> is the only place a
/// caller-supplied value is ever interpolated, so a value cannot reach a prompt unsanitised, and a
/// fourth write tool cannot forget.
/// </para>
/// <para>
/// The factories are per-scope rather than one constructor with everything optional, so a caller
/// cannot build a prompt whose scope and values disagree.
/// </para>
/// </remarks>
public sealed record WritePrompt
{
    private WritePrompt(WriteScope scope) => Scope = scope;

    /// <summary>What the confirmed write will reach.</summary>
    public WriteScope Scope { get; }

    private int DiagnosticIdCount { get; init; }

    private string Operation { get; init; } = string.Empty;

    private string Symbol { get; init; } = string.Empty;

    private string NewName { get; init; } = string.Empty;

    /// <summary>The prompt for <c>apply_fixes</c>: a count of diagnostic IDs, applied to one project.</summary>
    public static WritePrompt ForPrimaryProjectOf(int diagnosticIdCount) =>
        new(WriteScope.PrimaryProjectOf) { DiagnosticIdCount = diagnosticIdCount };

    /// <summary>The prompt for <c>edit_member</c>: one operation on one symbol, rewriting one file.</summary>
    public static WritePrompt ForSingleFile(string operation, string symbol) =>
        new(WriteScope.SingleFile) { Operation = operation, Symbol = symbol };

    /// <summary>The prompt for <c>rename_symbol</c>: one symbol renamed across every project.</summary>
    public static WritePrompt ForWholeSolution(string symbol, string newName) =>
        new(WriteScope.WholeSolution) { Symbol = symbol, NewName = newName };

    /// <summary>
    /// The sentence a human is asked to approve, around the already-resolved
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rendering per <see cref="WriteScope"/> member, all of them here. Each reproduces what its
    /// tool used to spell out for itself, and the reasoning that shaped it moved here with it:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <see cref="WriteScope.PrimaryProjectOf"/> is the only clause that branches on the target's
    /// extension (#149). <c>CodeFixService</c> narrows a solution to a single anchor project and
    /// fixes only that project's documents, so naming the solution outright would have the human
    /// authorise a write broader than the one about to happen — on a three-project solution, two are
    /// left untouched. A <c>.csproj</c> target <em>is</em> its own write scope, so it takes no
    /// qualifier. The anchor is deliberately not resolved and named: that costs an MSBuildWorkspace
    /// load before the human has been asked, and it would reopen the window PR #142 closed.
    /// </item>
    /// <item>
    /// <see cref="WriteScope.SingleFile"/> does <em>not</em> branch on the extension, because this
    /// write is one file either way. It claims neither of the two things that are not guaranteed:
    /// not "in '&lt;target&gt;'" — a <c>.csproj</c> does not bound the write, since
    /// <c>ProjectLoader</c> opens the containing solution and <c>SymbolResolver</c> searches every
    /// project in it, so "loaded from" is the true relation — and not "THE single file declaring
    /// it", since a partial type has several declarations and
    /// <c>DeclaringSyntaxReferences.FirstOrDefault()</c> picks one. It does branch on the
    /// <em>operation</em>, and only on the noun: <c>add</c> resolves the symbol as the container
    /// type (<c>CodeEditService.AddMember</c> rejects anything else), so calling it a member would
    /// name the human a thing that does not exist yet.
    /// </item>
    /// <item>
    /// <see cref="WriteScope.WholeSolution"/> takes no narrowing qualifier at all, and that is the
    /// counterweight that keeps the other two from reading as a blanket rule:
    /// <c>Renamer.RenameSymbolAsync</c> touches every changed project and every changed file, so
    /// naming the solution is exact and a "single file" qualifier here would be a fresh inaccuracy
    /// of the family #149/#154 closed.
    /// </item>
    /// </list>
    /// <para>
    /// Every caller-supplied value goes through <see cref="Sanitize"/> on its way in;
    /// <paramref name="target"/> deliberately does not (see that method's remarks).
    /// </para>
    /// </remarks>
    public string Render(string target) => Scope switch
    {
        WriteScope.PrimaryProjectOf => RenderPrimaryProjectOf(target),
        WriteScope.SingleFile => RenderSingleFile(target),
        WriteScope.WholeSolution =>
            $"Rename '{Sanitize(Symbol)}' to '{Sanitize(NewName)}' across the solution of "
            + $"'{target}' and write the changes to disk?",

        // Spelled out on purpose: a catch-all would quietly give a scope added later the wording of
        // whichever arm it fell into, which is how a human ends up approving a write described as
        // narrower than it is.
        _ => throw new ArgumentOutOfRangeException(
            nameof(Scope), Scope, "No confirmation sentence exists for this write scope."),
    };

    private string RenderPrimaryProjectOf(string target)
    {
        var qualifier = target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? "the primary project of "
            : string.Empty;
        return $"Apply code fixes for {DiagnosticIdCount} diagnostic ID(s) to {qualifier}'{target}' "
            + "and write the changes to disk?";
    }

    private string RenderSingleFile(string target)
    {
        var subject = Operation.Equals("add", StringComparison.OrdinalIgnoreCase)
            ? $"a member to type '{Sanitize(Symbol)}'"
            : $"member '{Sanitize(Symbol)}'";
        return $"Write the '{Sanitize(Operation)}' of {subject} to disk? Exactly one file is "
            + $"rewritten — the declaration it resolves to, anywhere in the code loaded from '{target}'.";
    }

    /// <summary>
    /// What a caller-supplied value renders as when it is empty or nothing but whitespace. Never an
    /// empty quoted run: <c>edit_member</c> does not validate <c>symbol</c>, so a whitespace-only one
    /// reaches the prompt, and "member ''" is the same unanswerable sentence PR #142 removed from the
    /// target side.
    /// </summary>
    private const string UnnamedValue = "(unnamed)";

    /// <summary>
    /// The maximum rendered length of a caller-supplied value. Generous beside any real
    /// fully-qualified name — <c>RoselineMCP.Services.CodeEditService.EditMemberAsync</c> is 51
    /// characters — and small enough that a payload cannot bury the rest of the sentence.
    /// </summary>
    private const int MaxValueLength = 120;

    /// <summary>
    /// A caller-supplied value, made safe to interpolate into the confirmation prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prompt is the last human checkpoint before a disk write, and two of the values it names —
    /// <c>symbol</c> and <c>newName</c> — are free-form caller input. Interpolated raw, a symbol
    /// carrying quote-and-punctuation rendered a complete, plausible sentence that ended before the
    /// real one began: the human read that first sentence, saw a scratch project, approved, and the
    /// write landed on the resolved target instead (#161a). A guard whose text is partly authored by
    /// the party being guarded is not a guard.
    /// </para>
    /// <para>
    /// Three rules, in order, each chosen to be <em>invisible</em> on every value a caller
    /// legitimately sends — a C# symbol reference carries no whitespace and no apostrophe, so an
    /// ordinary name comes back byte-for-byte:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Whitespace is removed, not collapsed.</b> Collapsing to single spaces still lets a payload
    /// read as prose ("… to disk? Exactly one file …"); removing it leaves one unbroken token no
    /// reader mistakes for the frame. It is lossless precisely because no identifier contains
    /// whitespace — which is what makes this safe to apply where a general-purpose escaper is not.
    /// </item>
    /// <item>
    /// <b>The apostrophe becomes U+2019.</b> The frame quotes with ASCII <c>'</c>, so that is the one
    /// character able to open or close a quoted run. Substituting rather than stripping keeps the
    /// value readable, and the substitute cannot be mistaken for a delimiter.
    /// </item>
    /// <item>
    /// <b>The length is capped, eliding the middle.</b> Both ends survive, so a long name stays
    /// recognisable: the head names the namespace, the tail the member.
    /// </item>
    /// </list>
    /// <para>
    /// The resolved <em>target</em> is deliberately NOT put through this. It is not caller-authored
    /// text but a path that has to exist on disk, and it is the one thing in the sentence a human can
    /// check against reality — eliding or re-punctuating it would break that
    /// (<c>ElicitationTests.ShouldNameARealProject</c> asserts <c>File.Exists</c> on it). A checkout
    /// path may legitimately contain an apostrophe — <c>C:\Users\O'Brien\src</c>, <c>~/Bob's
    /// Projects</c> — which is why every sentence above puts the target in its <em>last</em> quoted
    /// run: whatever a path contains, no frame text follows it to be forged past.
    /// </para>
    /// </remarks>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UnnamedValue;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            builder.Append(c == '\'' ? '\u2019' : c);
        }

        var sanitized = builder.ToString();
        if (sanitized.Length <= MaxValueLength)
        {
            return sanitized;
        }

        // Keep both ends, elide the middle: the head names the namespace, the tail the member.
        const int ellipsisLength = 1;
        var head = (MaxValueLength - ellipsisLength + 1) / 2;
        var tail = MaxValueLength - ellipsisLength - head;
        return string.Concat(sanitized.AsSpan(0, head), "…", sanitized.AsSpan(sanitized.Length - tail));
    }
}
