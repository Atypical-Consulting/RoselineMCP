using System.Globalization;
using System.Text;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Renders a <see cref="VerificationVerdict"/> into the text the compile guard hands back to an
/// agent after a file write — or into <see langword="null"/>, which means "say nothing".
/// </summary>
/// <remarks>
/// <para>
/// <b>Silence is the happy path.</b> The guard runs after every write, so the overwhelmingly common
/// verdict is "nothing broke" and it must cost zero tokens. Only an edit that <em>introduced</em>
/// compiler errors produces text: a verdict carrying only pre-existing errors renders to
/// <see langword="null"/>, because blaming an agent for the state of the branch it landed on is how
/// a guard turns into a degradation loop.
/// </para>
/// <para>
/// The output is capped at <see cref="MaxReportLength"/>, deliberately below the 10,000-character
/// limit the harness applies to hook feedback: being truncated by someone else means losing the
/// trailing explanation — the omitted count, the partial-scope warning — which is the part that
/// stops a short list from reading as an exhaustive one.
/// </para>
/// </remarks>
public static class GuardReportFormatter
{
    /// <summary>
    /// Hard ceiling on the rendered report, in characters. Below the harness's own 10,000-character
    /// cap on hook feedback so this formatter, and not the harness, decides what gets dropped.
    /// </summary>
    public const int MaxReportLength = 8_000;

    /// <summary>Head-room kept aside so the truncation notice itself always fits.</summary>
    private const int TruncationNoticeReserve = 128;

    /// <summary>
    /// Renders the verdict, or returns <see langword="null"/> when there is nothing worth an
    /// agent's attention.
    /// </summary>
    public static string? Format(VerificationVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var introduced = verdict.Introduced;
        if (introduced is null || introduced.Count == 0)
        {
            // Includes the already-red-branch case: compiles == false with a non-zero Preexisting
            // and nothing introduced is not this edit's problem, so the guard stays quiet.
            return null;
        }

        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"RoselineMCP compile guard — this edit introduced {introduced.Count} compiler {(introduced.Count == 1 ? "error" : "errors")}:");

        var trailer = BuildTrailer(verdict);
        var trailerText = string.Join('\n', trailer);

        // Fill the diagnostics list into whatever the header and trailer leave behind: the trailer
        // is the part that must never be dropped, so it is budgeted first.
        var budget = Math.Max(0, MaxReportLength - header.Length - trailerText.Length - TruncationNoticeReserve - 4);

        var lines = new StringBuilder();
        var shown = 0;
        foreach (var diagnostic in introduced)
        {
            var line = "\n  " + Render(diagnostic);
            if (lines.Length + line.Length > budget)
            {
                break;
            }

            lines.Append(line);
            shown++;
        }

        var builder = new StringBuilder(header).Append(lines);

        if (shown < introduced.Count)
        {
            builder.Append(
                CultureInfo.InvariantCulture,
                $"\n  … and {introduced.Count - shown} more, truncated to fit the feedback limit.");
        }

        builder.Append("\n\n").Append(trailerText);

        return Clamp(builder.ToString());
    }

    private static List<string> BuildTrailer(VerificationVerdict verdict)
    {
        var trailer = new List<string>();

        if (verdict.Omitted > 0)
        {
            trailer.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{verdict.Omitted} further diagnostic(s) were omitted from the verdict itself — this list is not exhaustive."));
        }

        if (!verdict.ScopeComplete)
        {
            // "partial", spelled out: a gate that could not see every dependent must never read
            // like one that did.
            trailer.Add("This verdict is partial — the workspace could not prove it holds every dependent of the changed projects.");
        }

        if (verdict.Notes is { Count: > 0 })
        {
            trailer.AddRange(verdict.Notes);
        }

        trailer.Add("Repair these before building further on them; check_compilation re-checks the whole solution.");

        return trailer;
    }

    private static string Render(DiagnosticDetail diagnostic) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Id}: {diagnostic.Message}");

    /// <summary>
    /// Last-resort clamp for the case the budget arithmetic cannot cover: verdict notes are
    /// arbitrary text and could on their own exceed the ceiling. Cuts back to a line boundary so a
    /// clamped report never ends mid-diagnostic.
    /// </summary>
    private static string Clamp(string text)
    {
        if (text.Length <= MaxReportLength)
        {
            return text;
        }

        var cut = text[..MaxReportLength];
        var lastBreak = cut.LastIndexOf('\n');
        return lastBreak > 0 ? cut[..lastBreak] : cut;
    }
}
