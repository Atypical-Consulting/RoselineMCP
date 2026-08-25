using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace RoselineMCP.Tests.Release;

/// <summary>
/// Pins every <b>hand-written</b> restatement of the headline token-saving median to the value
/// <c>RoselineMCP.TokenBenchmark</c> actually generated, in
/// <c>website/src/data/benchmark-results.json</c>.
/// <para>
/// <b>Why this exists.</b> Nine surfaces publish that median. Two <i>derive</i> it
/// (<c>index.astro</c>, <c>benchmark.astro</c>, via <c>Math.round(x * 100)</c>) and have never
/// drifted, because they cannot. The other seven restate it by hand, and re-measuring the benchmark
/// moves the number under all seven at once. That has now happened twice - at [2.1.1] and again
/// when the sweep grew from 568 to 670 tasks and the median moved 88.55% to 84.66% - and both times
/// a careful "did you update them all?" review pass announced completion and left something behind.
/// The second pass fixed <c>docs/AGENT-BENCHMARK.md</c>'s first occurrence and missed its second,
/// in the same file.
/// </para>
/// <para>
/// The failure mode is silence: the pooled figure (93%) did not move, so every stale surface still
/// gets half of its claim right and the mismatched half reads as a rounding difference. Nothing
/// builds, nothing runs, nothing goes red - the repository simply publishes a number its own
/// generated data does not support. This is the same class of guard as
/// <see cref="ReleaseWorkflowTests"/>: an assertion the build can evaluate, standing in for a
/// sentence in a changelog that asserted the surfaces were synced when nobody had checked.
/// </para>
/// <para>
/// <b>A re-measurement is supposed to fail every test in this class at once.</b> When the median
/// crosses a rounding boundary, that is not a broken test - it is the guard doing its only job.
/// Update the surfaces the failure messages name; do not loosen the assertions, and do not write
/// the expected percentage into this file. A test that hardcoded <c>85</c> would reproduce exactly
/// the defect it exists to prevent.
/// </para>
/// <para>
/// <b>Scope.</b> This class pins the <i>headline</i> median and nothing else. Two neighbouring
/// figures are deliberately out of its reach and must not be folded in: the pooled, size-weighted
/// 93%, which did not move with the median, and the separate end-to-end medians in
/// <c>docs/AGENT-BENCHMARK.md</c> (the ~50% forced-use ceiling and ~13% self-directed figures),
/// which measure a different thing entirely. That is why each claim below is anchored on the words
/// around it rather than on a bare percentage: an assertion keyed to "any two-digit figure near the
/// word median" would fail on those correct sentences and tell the reader to corrupt them.
/// </para>
/// </summary>
public class MedianFigureContractTests
{
    /// <summary>
    /// The generated source of truth. Everything else either derives from this file or is pinned
    /// against it below.
    /// </summary>
    private const string BenchmarkDataPath = "website/src/data/benchmark-results.json";

    /// <summary>
    /// The four sites in the README, including the shields.io badge - whose figure is
    /// <b>URL-encoded</b> (<c>%25</c> for the percent sign), so a reader looking for "85%" in the
    /// raw file does not find it and a careless sweep skips the badge. The badge is the first
    /// number anyone sees on the repository page.
    /// </summary>
    [Fact]
    public void Readme_Should_State_The_Generated_Median()
    {
        AssertClaim("README.md", "Measured {median}% fewer tokens (median)", 1);
        AssertClaim("README.md", "tokens-{median}%25_fewer_(median)", 1);
        AssertClaim("README.md", "**median {median}% fewer tokens per task**", 1);
        AssertClaim("README.md", "**{median}% median** token reduction per task", 1);
    }

    /// <summary>
    /// <c>BENCHMARKS.md</c> introduces the three benchmarks by naming what each one measures, so its
    /// median <i>is</i> the definition a reader carries into the rest of the docs. The neighbouring
    /// pooled figure (93%) is correct and is deliberately not pinned here - it did not move when the
    /// median did, which is precisely why the drift stayed invisible.
    /// </summary>
    [Fact]
    public void Benchmarks_Doc_Should_State_The_Generated_Median()
    {
        AssertClaim("BENCHMARKS.md", "**{median}% median** headline", 1);
    }

    /// <summary>
    /// <b>Two</b> occurrences, asserted by count rather than by "contains". This file is where the
    /// bug this class guards actually lived: line 5 was corrected and line 52 was not, leaving the
    /// document contradicting itself. A <c>ShouldContain</c> assertion passes that state, so it
    /// would have shipped the defect while reporting green.
    /// </summary>
    [Fact]
    public void AgentBenchmark_Doc_Should_State_The_Generated_Median_In_Both_Places()
    {
        AssertClaim("docs/AGENT-BENCHMARK.md", "**median {median}%** reduction per task", 1);
        AssertClaim("docs/AGENT-BENCHMARK.md", "per-call savings ({median}% median)", 1);
    }

    /// <summary>
    /// The site's default meta description and the <c>og:image:alt</c> that describes the social
    /// card. The alt text is the one machine-readable claim about <c>website/public/og.png</c>, an
    /// image no test can read - so if these two ever disagree with the card, the alt text is the
    /// only side a build can see.
    /// </summary>
    [Fact]
    public void Site_Layout_Should_State_The_Generated_Median_In_Both_Meta_Sites()
    {
        AssertClaim("website/src/layouts/Base.astro", "{median}% fewer tokens (median)", 2);
    }

    /// <summary>
    /// <c>long_description</c> ships <i>inside</i> the <c>.mcpb</c> bundle attached to every GitHub
    /// Release - it is the copy a user reads in their client while deciding whether to install. A
    /// stale figure here is an overclaim that travels further from the repository than any other,
    /// and it is reviewed by nobody, because the file is otherwise touched only by release-please's
    /// version bump.
    /// </summary>
    [Fact]
    public void Mcpb_Manifest_Should_State_The_Generated_Median()
    {
        AssertClaim("mcpb/manifest.json", "measured ~{median}% fewer tokens, median", 1);
    }

    /// <summary>
    /// The template <c>website/public/og.png</c> is rendered from - the figure in every link
    /// preview, social post and chat unfurl. This assertion pins the <b>template only</b>: the
    /// rendered PNG is binary and is regenerated by hand, so a correct template with a stale image
    /// still passes. That residual gap is real and is documented rather than papered over; see the
    /// note in <c>BENCHMARKS.md</c>.
    /// </summary>
    [Fact]
    public void OgCard_Template_Should_State_The_Generated_Median()
    {
        AssertClaim("website/og-card.html", "<div class=\"stat\">{median}<span class=\"pct\">%</span></div>", 1);
    }

    /// <summary>
    /// Pins the per-file outline savings and the outline suite's own median in
    /// <c>benchmark.astro</c>'s "where the file outline wins - and where it can't" callout, which
    /// names <c>Program.cs</c>, <c>CodeFixService.cs</c> and <c>IDiagnosticFilterService.cs</c> and
    /// restates each one's saving by hand. These four figures come from the <c>outline</c> suite's
    /// own rows and aggregate in <see cref="BenchmarkDataPath"/> - not from
    /// <c>headline.medianSavingsReadTools</c> - so a re-measurement can move them independently of
    /// the headline median every other test in this class pins, and nothing else here would catch
    /// them drifting.
    /// </summary>
    [Fact]
    public void Benchmark_Page_Should_State_The_Generated_Outline_Figures()
    {
        var figures = CurrentFigures.Value;
        const string relativePath = "website/src/pages/benchmark.astro";

        // benchmark.astro's own pct() helper signs the magnitude explicitly - ASCII '+' or the
        // U+2212 MINUS SIGN used throughout this class - and never emits a bare number, so each
        // claim below fixes the sign as a template literal and sweeps only the two-digit magnitude.
        AssertClaim(
            relativePath,
            "to <span class=\"save-ink\">+{value}%</span>.</p>",
            "{value}",
            figures.OutlineMedianSavingsPercent,
            1);

        AssertClaim(
            relativePath,
            "<code>Program.cs</code> <span class=\"save-ink\">+{value}%</span>",
            "{value}",
            figures.ProgramCs.SavingsPercent,
            1);

        AssertClaim(
            relativePath,
            "<code>CodeFixService.cs</code> <span class=\"save-ink\">+{value}%</span>",
            "{value}",
            figures.CodeFixService.SavingsPercent,
            1);

        AssertClaim(
            relativePath,
            "<code>IDiagnosticFilterService.cs</code> <span class=\"cost-ink\">−{value}%</span>",
            "{value}",
            Math.Abs(figures.DiagnosticFilterService.SavingsPercent),
            1);
    }

    /// <summary>
    /// Pins the home page's "rose-line transform" showcase - the <c>Program.cs</c> before/after that
    /// is the first concrete number a visitor sees. The same <c>outline</c> suite row is restated by
    /// hand four times: the lede's prose, the figure's <c>aria-label</c> (the only copy of this
    /// claim a screen-reader user hears - the visual panels beside it are not read to them), the
    /// "whole file" panel's own cost line, and the outline panel's head, which carries both halves of
    /// the claim - the token count and the percentage together. A re-measurement can leave any subset
    /// of the four stale relative to the others, which is exactly the silent-drift shape this class
    /// exists to catch.
    /// </summary>
    [Fact]
    public void Index_Page_Should_State_The_Generated_ProgramCs_Transform()
    {
        var programCs = CurrentFigures.Value.ProgramCs;
        var wholeFile = FormatTokenCount(programCs.WholeFileTokens);
        var tool = FormatTokenCount(programCs.ToolTokens);
        var magnitude = Math.Abs(programCs.SavingsPercent);
        const string relativePath = "website/src/pages/index.astro";

        AssertLiteralClaim(
            relativePath,
            $"this repo: <strong>{wholeFile} tokens become {tool}</strong>",
            1);

        AssertLiteralClaim(
            relativePath,
            $"aria-label=\"RoselineMCP turns a {wholeFile}-token file into a {tool}-token outline.\"",
            1);

        AssertLiteralClaim(
            relativePath,
            $"<span class=\"mono\">Program.cs · the whole file</span><span class=\"tp-cost mono\">{wholeFile} tokens</span>",
            1);

        AssertLiteralClaim(
            relativePath,
            $"<span class=\"tp-save mono\">{tool}&nbsp;tokens</span><span class=\"pill write\">−{magnitude}%</span>",
            1);
    }

    /// <summary>
    /// Pins the README's pull-quote - the line most likely to be copy-pasted elsewhere by someone
    /// deciding whether to try the tool. It uses a sign convention different from every other
    /// surface in this class: it states the figure as a magnitude of reduction
    /// ("2,093 tokens → 120 (−94%)"), always with the U+2212 minus, even though the same
    /// <c>outline</c> row's <c>savingsVsWholeFilePct</c> is a <i>positive</i> saving (0.9427). Read
    /// the sign off the surface being pinned, not off the JSON - that asymmetry is the easiest thing
    /// to get subtly wrong here, which is why it is called out rather than folded silently into a
    /// shared formatter with <see cref="Benchmark_Page_Should_State_The_Generated_Outline_Figures"/>.
    /// </summary>
    [Fact]
    public void Readme_Should_State_The_Generated_ProgramCs_PullQuote()
    {
        var programCs = CurrentFigures.Value.ProgramCs;
        var wholeFile = FormatTokenCount(programCs.WholeFileTokens);
        var tool = FormatTokenCount(programCs.ToolTokens);
        var magnitude = Math.Abs(programCs.SavingsPercent);

        AssertLiteralClaim(
            "README.md",
            $"on `Program.cs`: **{wholeFile} tokens → {tool}** (−{magnitude}%).",
            1);
    }

    /// <summary>
    /// Asserts that <paramref name="claimTemplate"/>, with the generated median substituted for
    /// <c>{median}</c>, occurs in <paramref name="relativePath"/> exactly
    /// <paramref name="expectedOccurrences"/> times - <b>and</b> that the same claim carrying any
    /// other two-digit figure occurs nowhere in that file. The second half is what distinguishes
    /// this from a <c>ShouldContain</c>: a file that states the current median once and a
    /// superseded one somewhere else is exactly the defect being guarded, and "contains" passes it.
    /// Delegates to <see cref="AssertClaim(string,string,string,int,int)"/>, which generalises the
    /// placeholder and the figure it stands for so a claim pinning a different generated figure (a
    /// per-file saving, a suite's own median) does not need a second copy of this method's body.
    /// </summary>
    private static void AssertClaim(string relativePath, string claimTemplate, int expectedOccurrences) =>
        AssertClaim(relativePath, claimTemplate, "{median}", CurrentMedianPercent.Value, expectedOccurrences);

    /// <summary>
    /// The shared core <see cref="AssertClaim(string,string,int)"/> delegates to: fills
    /// <paramref name="placeholder"/> in <paramref name="claimTemplate"/> with
    /// <paramref name="value"/>, asserts the result occurs <paramref name="expectedOccurrences"/>
    /// times, and sweeps every <b>other</b> two-digit figure through the same placeholder to prove no
    /// stale variant of the claim survives - the load-bearing negative half described on
    /// <see cref="AssertClaim(string,string,int)"/>. Every caller anchors
    /// <paramref name="claimTemplate"/> on the words around the figure, per the class summary's
    /// Scope note, rather than on a bare percentage.
    /// </summary>
    private static void AssertClaim(string relativePath, string claimTemplate, string placeholder, int value, int expectedOccurrences)
    {
        // Both sides are whitespace-normalised before matching. These are hard-wrapped Markdown/HTML
        // files, so a claim routinely straddles a newline (README's "median 85% fewer tokens / per
        // task" does today). Matching raw text would couple every assertion to the current wrap:
        // re-flowing a paragraph would fail the guard for no real reason, and — worse — a stale
        // figure written across a line break would slip past the sweep below unseen.
        var text = Normalize(ReadRepoFile(relativePath));
        var claim = Normalize(Fill(claimTemplate, placeholder, value));

        Occurrences(text, claim).ShouldBe(
            expectedOccurrences,
            $"{relativePath} should carry \"{claim}\" exactly {expectedOccurrences} time(s) - the generated figure in {BenchmarkDataPath} is {value}.");

        // Only two-digit figures are swept: every claim this core backs is a percentage (10-99), and
        // the generated figures have never been outside that range. A future figure of 9 or 100
        // would still be pinned by the positive assertion above; this loop would simply have nothing
        // stale to find.
        for (var other = 10; other <= 99; other++)
        {
            if (other == value)
            {
                continue;
            }

            var stale = Normalize(Fill(claimTemplate, placeholder, other));

            Occurrences(text, stale).ShouldBe(
                0,
                $"{relativePath} still carries \"{stale}\", but the generated figure in {BenchmarkDataPath} is {value}. Update the surface, not this test.");
        }
    }

    /// <summary>
    /// The positive half of <see cref="AssertClaim(string,string,string,int,int)"/> alone, for a
    /// claim that is already fully filled in (typically with token counts) rather than pinning a
    /// single two-digit percentage - a 10-99 sweep has nothing meaningful to enumerate over a
    /// four-digit token count. Reuses the same file access and whitespace normalisation as every
    /// other assertion in this class rather than opening <paramref name="relativePath"/> a second
    /// way.
    /// </summary>
    private static void AssertLiteralClaim(string relativePath, string claim, int expectedOccurrences)
    {
        var text = Normalize(ReadRepoFile(relativePath));
        var normalizedClaim = Normalize(claim);

        Occurrences(text, normalizedClaim).ShouldBe(
            expectedOccurrences,
            $"{relativePath} should carry \"{normalizedClaim}\" exactly {expectedOccurrences} time(s), matching the figures generated in {BenchmarkDataPath}.");
    }

    /// <summary>
    /// The generated median as a whole percentage, rounded the way the website rounds it - see
    /// <see cref="SiteRoundPercent"/> for the rule itself.
    /// <para>
    /// Read once for the whole class: the benchmark data is a ~550 KB document and cannot change
    /// during a run, so parsing it per assertion would re-parse it nine times for one number.
    /// </para>
    /// </summary>
    private static readonly Lazy<int> CurrentMedianPercent = new(() =>
    {
        using var document = JsonDocument.Parse(ReadRepoFile(BenchmarkDataPath));

        var median = document.RootElement
            .GetProperty("headline")
            .GetProperty("medianSavingsReadTools")
            .GetDouble();

        return SiteRoundPercent(median);
    });

    /// <summary>
    /// The per-file outline savings and the outline suite's own median, pinned by
    /// <see cref="Benchmark_Page_Should_State_The_Generated_Outline_Figures"/>,
    /// <see cref="Index_Page_Should_State_The_Generated_ProgramCs_Transform"/> and
    /// <see cref="Readme_Should_State_The_Generated_ProgramCs_PullQuote"/>. Extracted once, like
    /// <see cref="CurrentMedianPercent"/> above and for the same reason - a second, independent
    /// <c>Lazy&lt;&gt;</c> rather than folding into it, so a fact that only needs the median does not
    /// pay for resolving suite rows it never reads.
    /// </summary>
    private static readonly Lazy<BenchmarkFigures> CurrentFigures = new(() =>
    {
        using var document = JsonDocument.Parse(ReadRepoFile(BenchmarkDataPath));

        var outline = ResolveSuite(document, "outline");

        return new BenchmarkFigures(
            ProgramCs: ResolveSuiteRow(outline, "RoselineMCP/Program.cs"),
            CodeFixService: ResolveSuiteRow(outline, "/Services/CodeFixService.cs"),
            DiagnosticFilterService: ResolveSuiteRow(outline, "RoselineMCP/Interfaces/IDiagnosticFilterService.cs"),
            OutlineMedianSavingsPercent: SiteRoundPercent(outline.GetProperty("aggregate").GetProperty("medianSavingsVsWholeFile").GetDouble()));
    });

    /// <summary>
    /// What <see cref="CurrentFigures"/> extracts from the <c>outline</c> suite: one named row each
    /// for <c>Program.cs</c>, <c>CodeFixService.cs</c> and <c>IDiagnosticFilterService.cs</c>, plus
    /// the suite's own median saving.
    /// </summary>
    private sealed record BenchmarkFigures(
        SuiteRowFigure ProgramCs,
        SuiteRowFigure CodeFixService,
        SuiteRowFigure DiagnosticFilterService,
        int OutlineMedianSavingsPercent);

    /// <summary>
    /// One <c>rows[]</c> entry's saving (site-rounded, signed) and both token counts - what
    /// benchmark.astro, index.astro and the README each restate by hand for a named file.
    /// </summary>
    private sealed record SuiteRowFigure(int SavingsPercent, int WholeFileTokens, int ToolTokens);

    /// <summary>
    /// Resolves a <c>suites[]</c> entry by <c>id</c>.
    /// </summary>
    private static JsonElement ResolveSuite(JsonDocument document, string suiteId) =>
        document.RootElement
            .GetProperty("suites")
            .EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == suiteId);

    /// <summary>
    /// Resolves the one row in <paramref name="suite"/> whose <c>target</c> ends with
    /// <paramref name="targetSuffix"/>, and fails loudly rather than silently picking a wrong match
    /// if more than one does. That guard is not theoretical here:
    /// <c>RoselineMCP/Interfaces/ICodeFixService.cs</c> also ends with the literal string
    /// <c>CodeFixService.cs</c>, so a naive <c>EndsWith("CodeFixService.cs")</c> lookup would match
    /// it too and could silently resolve to the wrong row depending on array order.
    /// <see cref="CurrentFigures"/> avoids that by passing the longer, unambiguous suffix
    /// <c>"/Services/CodeFixService.cs"</c> - this check exists so a future caller who does not is
    /// told loudly instead of getting a plausible-looking wrong figure.
    /// </summary>
    private static SuiteRowFigure ResolveSuiteRow(JsonElement suite, string targetSuffix)
    {
        var matches = suite.GetProperty("rows")
            .EnumerateArray()
            .Where(r => r.GetProperty("target").GetString()!.EndsWith(targetSuffix, StringComparison.Ordinal))
            .ToArray();

        matches.Length.ShouldBe(
            1,
            $"expected exactly one row whose target ends with \"{targetSuffix}\" in {BenchmarkDataPath}, found {matches.Length}.");

        var row = matches[0];

        return new SuiteRowFigure(
            SavingsPercent: SiteRoundPercent(row.GetProperty("savingsVsWholeFilePct").GetDouble()),
            WholeFileTokens: row.GetProperty("wholeFile").GetProperty("tokens").GetInt32(),
            ToolTokens: row.GetProperty("tool").GetProperty("tokens").GetInt32());
    }

    /// <summary>
    /// The single rounding rule in this file. The site uses JavaScript's <c>Math.round</c>, which
    /// breaks ties upward rather than to even, so <c>Math.Floor(x + 0.5)</c> is used here instead of
    /// <c>Math.Round</c> - otherwise the test and the page it guards could disagree about a value
    /// landing exactly on .5. Every figure this class pins - the headline median and each per-file
    /// or per-suite outline saving - goes through this one method so the rule cannot drift between
    /// them.
    /// </summary>
    private static int SiteRoundPercent(double fraction) => (int)Math.Floor((fraction * 100) + 0.5);

    /// <summary>
    /// Formats a token count the way every site does: thousands-separated, no decimal - "2,093" not
    /// "2093". Shared so the README and index.astro claims agree on formatting with each other and
    /// with the chart/headline stats elsewhere on the site.
    /// </summary>
    private static string FormatTokenCount(int tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Collapses every run of whitespace to a single space so a claim matches regardless of where
    /// the file happens to wrap. See the comment in <see cref="AssertClaim(string,string,string,int,int)"/>
    /// for why that coupling would otherwise be a hole in the guard rather than merely an
    /// inconvenience.
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Substitutes the figure by plain replacement rather than <c>string.Format</c>: the pinned
    /// claims include HTML and JSON fragments, and a stray brace in one of them would turn a
    /// missing assertion into a format exception.
    /// </summary>
    private static string Fill(string claimTemplate, string placeholder, int value) =>
        claimTemplate.Replace(placeholder, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;

        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(RepoPath(relativePath));

    /// <summary>
    /// Resolves a repository-relative path from this source file's compile-time location - the same
    /// idiom <see cref="ReleaseWorkflowTests"/> and <c>ToolSchemaSnapshotTests</c> use. This file
    /// lives at <c>RoselineMCP.Tests/Release/</c>, so the repository root is two levels up.
    /// </summary>
    private static string RepoPath(string relativePath, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", relativePath));
}
