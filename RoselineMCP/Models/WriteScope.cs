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
    /// One shape is common to all three and is <b>load-bearing rather than stylistic</b>:
    /// <paramref name="target"/> is the <b>last quoted run of the sentence</b>. Each prompt asks its
    /// question first and states its scope after, so nothing follows the path. That is what lets the
    /// target stay verbatim (see <see cref="Sanitize"/>'s remarks): a checkout directory may
    /// legitimately contain an apostrophe, which closes the quoted run early, and with the sentence
    /// ending there it has no trailing clause left to forge.
    /// <see cref="WriteScope.PrimaryProjectOf"/> and <see cref="WriteScope.WholeSolution"/> used to
    /// name the target mid-sentence and end "… and write the changes to disk?", which made that
    /// clause forgeable by a <em>directory</em> name the same way <c>symbol</c> once was (#173);
    /// they were re-worded to <see cref="WriteScope.SingleFile"/>'s shape, and the re-wording moved
    /// where each claim sits without changing what any of them claims. A fourth scope must keep the
    /// property — <c>ElicitationTests.Every_Write_Prompt_Ends_On_Its_Target</c> is exhaustive over
    /// the enum, so one that does not fails there rather than shipping.
    /// </para>
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
            $"Rename '{Sanitize(Symbol)}' to '{Sanitize(NewName)}' and write the changes to disk? "
            + $"The write reaches every project across the solution of '{target}'.",

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
        return $"Apply code fixes for {DiagnosticIdCount} diagnostic ID(s) and write the changes to "
            + $"disk? The write reaches {qualifier}'{target}'.";
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
    /// It is a <b>whitelist</b>, and that shape is the point. The first attempt at this was a
    /// denylist — remove <c>char.IsWhiteSpace</c>, swap ASCII <c>'</c> — and a denylist cannot work
    /// here, because the reader being protected is a <em>human</em> and the alphabet of characters
    /// that look like a space or a quote to a human is open-ended. U+2800 BRAILLE PATTERN BLANK and
    /// U+3164 HANGUL FILLER both render as blanks and are not <c>IsWhiteSpace</c> (U+3164 is
    /// categorised as a <em>letter</em>); U+200B is neither whitespace nor visible; and a U+2019
    /// supplied directly by the caller passed straight through, indistinguishable from the frame's
    /// own quotes at a glance. Each one rebuilt the forged sentence in characters the denylist did
    /// not name.
    /// </para>
    /// <para>
    /// A whitelist inverts the burden: these values are C# symbol references, so everything a
    /// legitimate one can contain is enumerable (<see cref="IsSymbolReferenceChar"/>) and everything
    /// else — every space-alike, every quote-alike, every <c>?</c> and dash a sentence needs — is
    /// dropped without having to be anticipated. What survives is one unbroken identifier-shaped
    /// token that no reader mistakes for the frame. An ordinary name is untouched, so the sanitiser
    /// is invisible in the normal case. Then the length is capped, eliding the middle so both ends
    /// survive and a long name stays recognisable: the head names the namespace, the tail the member.
    /// </para>
    /// <para>
    /// The resolved <em>target</em> is deliberately NOT put through this, and the reason is that it
    /// is not caller-authored text: it is a path resolved from the file system, and it is the one
    /// thing in the sentence a human can check against reality. Eliding or re-punctuating it would
    /// break exactly that (<c>ElicitationTests.ShouldNameARealProject</c> asserts
    /// <c>File.Exists</c> on it), so it is rendered verbatim.
    /// </para>
    /// <para>
    /// Leaving it verbatim is safe because of where it sits, not because of what it contains: the
    /// target is the <b>last quoted run of every sentence</b> (<see cref="Render"/>). A checkout path
    /// may legitimately contain an apostrophe — <c>C:\Users\O'Brien\src</c>, <c>~/Bob's Projects</c> —
    /// which closes the quoted run early; with nothing following it, the worst that does is render
    /// the path oddly, and there is no trailing clause to forge.
    /// </para>
    /// <para>
    /// That was not always true, and the history is the reason it is stated here rather than assumed.
    /// <see cref="WriteScope.PrimaryProjectOf"/> and <see cref="WriteScope.WholeSolution"/> both used
    /// to end "… and write the changes to disk?" <em>after</em> the target, which made that clause
    /// forgeable by a <em>directory</em> name — the operator's filesystem reaching the same hole
    /// #161a closed on the caller's side. #170 corrected the claim; #173 closed the hole, by
    /// re-ordering the two sentences rather than by escaping the path, so that nothing had to be
    /// traded away here.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether a character can appear in a C# symbol reference as these tools accept one: a letter
    /// or digit in any script, plus the punctuation that qualifies, parameterises or decorates a
    /// name — <c>Acme.Orders.Repository&lt;T,U&gt;</c>, <c>@class</c>, <c>List`1</c>,
    /// <c>global::Acme</c>, <c>Outer+Inner</c>.
    /// </summary>
    /// <remarks>
    /// Surrogate halves are category <c>Cs</c> and so fall outside this, which drops astral-plane
    /// identifiers from the *display* — an acceptable loss, and one that removes the possibility of
    /// <see cref="Sanitize"/>'s mid-string elision splitting a surrogate pair.
    /// </remarks>
    private static bool IsSymbolReferenceChar(char c) =>
        (char.IsLetterOrDigit(c) || c is '.' or '_' or '<' or '>' or ',' or '@' or '`' or ':' or '+')
        && !IsBlankRenderingLetter(c);

    /// <summary>
    /// The four Hangul fillers — U+115F, U+1160, U+3164, U+FFA0 — which Unicode categorises as
    /// <em>letters</em> (Lo) while they render as blanks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A denylist inside the whitelist, and deliberately so: the whitelist's premise is "a character
    /// a symbol reference can contain, which renders as itself", and .NET's character categories are
    /// the only available stand-in for the second half. These four are where that stand-in is wrong
    /// — <c>char.IsLetterOrDigit('\u3164')</c> returns <see langword="true"/> — so a payload could
    /// use them as word separators and rebuild a readable sentence out of characters the whitelist
    /// had just admitted. The first version of this whitelist did exactly that, and the test that
    /// pins it (<c>Render_Drops_Look_Alike_Characters_A_Symbol_Reference_Cannot_Contain</c>) is what
    /// caught it.
    /// </para>
    /// <para>
    /// The list is closed rather than open-ended: among Unicode's default-ignorable code points,
    /// these are the only ones that are also letters or digits. Everything else that renders blank —
    /// U+200B, U+2060, U+FEFF, U+2800 — is already outside the whitelist by category.
    /// </para>
    /// </remarks>
    private static bool IsBlankRenderingLetter(char c) =>
        c is '\u115F' or '\u1160' or '\u3164' or '\uFFA0';

    internal static string Sanitize(string? value)
    {
        if (value is null)
        {
            return UnnamedValue;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (IsSymbolReferenceChar(c))
            {
                builder.Append(c);
            }
        }

        var sanitized = builder.ToString();
        if (sanitized.Length == 0)
        {
            return UnnamedValue;
        }

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
