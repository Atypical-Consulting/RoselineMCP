using System.Globalization;
using System.Runtime.CompilerServices;
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
        AssertClaim("README.md", "median {median}% fewer tokens", 1);
        AssertClaim("README.md", "**{median}% median**", 1);
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
        AssertClaim("BENCHMARKS.md", "**{median}% median**", 1);
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
        AssertClaim("docs/AGENT-BENCHMARK.md", "**median {median}%**", 1);
        AssertClaim("docs/AGENT-BENCHMARK.md", "({median}% median)", 1);
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
    /// Asserts that <paramref name="claimTemplate"/>, with the generated median substituted for
    /// <c>{median}</c>, occurs in <paramref name="relativePath"/> exactly
    /// <paramref name="expectedOccurrences"/> times - <b>and</b> that the same claim carrying any
    /// other two-digit figure occurs nowhere in that file. The second half is what distinguishes
    /// this from a <c>ShouldContain</c>: a file that states the current median once and a
    /// superseded one somewhere else is exactly the defect being guarded, and "contains" passes it.
    /// </summary>
    private static void AssertClaim(string relativePath, string claimTemplate, int expectedOccurrences)
    {
        var text = ReadRepoFile(relativePath);
        var median = CurrentMedianPercent();
        var claim = Fill(claimTemplate, median);

        Occurrences(text, claim).ShouldBe(
            expectedOccurrences,
            $"{relativePath} should carry \"{claim}\" exactly {expectedOccurrences} time(s) - the median generated in {BenchmarkDataPath} is {median}%.");

        // Only two-digit figures are swept: every published claim is one, and the generated median
        // has never been outside that range. A future median of 9% or 100% would still be pinned by
        // the positive assertion above; this loop would simply have nothing stale to find.
        for (var other = 10; other <= 99; other++)
        {
            if (other == median)
            {
                continue;
            }

            var stale = Fill(claimTemplate, other);

            Occurrences(text, stale).ShouldBe(
                0,
                $"{relativePath} still carries \"{stale}\", but the generated median in {BenchmarkDataPath} is {median}%. Update the surface, not this test.");
        }
    }

    /// <summary>
    /// The generated median as a whole percentage, rounded the way the website rounds it. The site
    /// uses JavaScript's <c>Math.round</c>, which breaks ties upward rather than to even, so
    /// <c>Math.Floor(x + 0.5)</c> is used here instead of <c>Math.Round</c> - otherwise the test and
    /// the page it guards could disagree about a value landing exactly on .5.
    /// </summary>
    private static int CurrentMedianPercent()
    {
        using var document = JsonDocument.Parse(ReadRepoFile(BenchmarkDataPath));

        var median = document.RootElement
            .GetProperty("headline")
            .GetProperty("medianSavingsReadTools")
            .GetDouble();

        return (int)Math.Floor((median * 100) + 0.5);
    }

    /// <summary>
    /// Substitutes the figure by plain replacement rather than <c>string.Format</c>: the pinned
    /// claims include HTML and JSON fragments, and a stray brace in one of them would turn a
    /// missing assertion into a format exception.
    /// </summary>
    private static string Fill(string claimTemplate, int median) =>
        claimTemplate.Replace("{median}", median.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

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
