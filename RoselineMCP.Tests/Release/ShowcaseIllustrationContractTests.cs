using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Shouldly;

namespace RoselineMCP.Tests.Release;

/// <summary>
/// Pins the "outline it returns" panel in <c>website/src/pages/index.astro</c> to the real
/// <c>RoselineMCP/Program.cs</c> it illustrates.
/// <para>
/// <b>Why this exists.</b> The panel is hand-typed markup with no data import, so nothing fails when
/// <c>Program.cs</c> moves out from under it. It drifted once (#212: wrong line numbers and a
/// fictitious instance constructor row) and was corrected by hand; this test is the guard rail for
/// the next time. If it fails after an unrelated edit to <c>Program.cs</c>, update the
/// <c>oline</c> numbers (or rows) in <c>index.astro</c> to match - do not widen the tolerance.
/// </para>
/// <para>
/// Each row is located in <c>Program.cs</c> by <i>name</i> and compared within a window, so ordinary
/// drift does not break it; no line number is the sole pass condition.
/// </para>
/// </summary>
public partial class ShowcaseIllustrationContractTests
{
    private const int Tolerance = 5;

    [GeneratedRegex("""<span class="okind">(?<kind>[^<]+)</span><span class="osig mono">(?<sig>[^<]+)</span><span class="oline mono">:(?<line>\d+)</span>""")]
    private static partial Regex OutlineRow();

    [Fact]
    public void Outline_Rows_Should_Match_Program_Cs()
    {
        var rows = OutlineRow().Matches(File.ReadAllText(RepoPath("website/src/pages/index.astro")))
            .Select(m => (Kind: m.Groups["kind"].Value, Sig: m.Groups["sig"].Value,
                Line: int.Parse(m.Groups["line"].Value)))
            .ToList();
        rows.ShouldNotBeEmpty("the showcase outline panel was not found in index.astro");

        var source = File.ReadAllLines(RepoPath("RoselineMCP/Program.cs"));

        foreach (var (kind, sig, claimed) in rows)
        {
            var isStatic = Regex.IsMatch(sig, @"\bstatic\b");
            var declaration = kind == "class"
                ? $@"\bclass\s+{Regex.Escape(sig.Trim())}\b"
                : Declaration(sig, isStatic);

            var actual = Enumerable.Range(0, source.Length)
                .Where(i => Regex.IsMatch(source[i], declaration))
                .Select(i => i + 1)
                .ToList();

            actual.ShouldNotBeEmpty($"outline row '{kind} {sig}' (:{claimed}) has no matching declaration in Program.cs");
            actual.Min(l => Math.Abs(l - claimed)).ShouldBeLessThanOrEqualTo(Tolerance,
                $"outline row '{kind} {sig}' claims :{claimed} but Program.cs declares it at {string.Join(", ", actual)}");
        }
    }

    // A declaration starts with a modifier (so calls and usages never match); a static member must
    // say so, and an instance row must NOT match a static declaration - the #212 phantom ctor.
    private static string Declaration(string sig, bool isStatic)
    {
        var name = Regex.Match(sig, @"(\w+)\s*\(").Groups[1].Value;
        name.ShouldNotBeEmpty($"cannot read a member name from outline signature '{sig}'");
        return isStatic
            ? $@"^\s*(\w+\s+)*static\s[^=;]*\b{name}\s*\("
            : $@"^\s*(public|private|internal|protected)\s+(?!static\b)[^=;]*\b{name}\s*\(";
    }

    private static string RepoPath(string relativePath, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", relativePath));
}
