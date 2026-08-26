using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RoselineMCP.Tests.Support;
using Shouldly;

namespace RoselineMCP.Tests.Release;

/// <summary>
/// Pins <c>website/src/data/tools.ts</c> to the reflected <c>[McpServerTool]</c> set (#208) — the
/// last unguarded link in the tool-count chain. #197 was the site's headings contradicting its own
/// grid; #206 fixed that by making both derive from this array. Neither did anything about the array
/// itself: it is hand-maintained, and nothing tied it back to the server's actual tool set, so a 15th
/// tool could land in C#, bump <see cref="Protocol.ToolDescriptionContractTests"/>'s count
/// deliberately, and still never reach the site — internally consistent, externally wrong, nothing
/// red anywhere. That is exactly how #197 happened one link downstream: <c>check_compilation</c>
/// landed as tool 14 in #133 and the doc-alignment pass updated <c>tools.ts</c> but not the prose
/// above it.
/// </summary>
/// <remarks>
/// Sibling of <see cref="MedianFigureContractTests"/>, pinning a different hand-written surface for
/// the same reason its own class doc names: <b>the failure mode is silence.</b> A mismatch here is a
/// red test naming the file and the differing names — never a page that quietly diverges from its
/// own source of truth.
/// <para>
/// The comparison is over <b>names</b>, not a count, so a rename fails exactly as an addition or a
/// removal does — a bare count assertion would let <c>get_symbol_info</c> silently become
/// <c>get_symbol_details</c> in the C# and stay <c>get_symbol_info</c> on the site forever.
/// </para>
/// </remarks>
public class WebsiteToolsDataContractTests
{
    private const string ToolsDataPath = "website/src/data/tools.ts";

    /// <summary>
    /// Matches a <c>tools</c> array entry's <c>name: '…'</c> field — single-quoted, so the
    /// <c>Tool</c> interface's own unquoted <c>name: string;</c> declaration can never match. #197's
    /// own repro documented the trap this excludes: a bare <c>name:</c> match returns 15 hits for a
    /// 14-tool array because it also counts the interface field — see the class doc on
    /// <see cref="ParseWireNames"/>.
    /// </summary>
    private static readonly Regex WireNamePattern = new(@"name:\s*'([a-z0-9_]+)'", RegexOptions.Compiled);

    [Fact]
    public void Website_Tools_Data_Matches_The_Reflected_Tool_Set()
    {
        var reflected = ReflectedTools.WireNames();
        var published = ParseWireNames(ReadRepoFile(ToolsDataPath));

        published.ShouldBe(reflected, ignoreOrder: true,
            $"{ToolsDataPath} no longer matches the [McpServerTool] set reflected from the server " +
            "assembly. Add, remove or rename the entry there to match — the site's headings and " +
            "grid both derive from this array (#197, fixed by #206), and this test is the only " +
            "thing that keeps the array itself honest (#208).");
    }

    /// <summary>
    /// Parses every <c>name: '…'</c> value out of <paramref name="source"/>. Not a TypeScript
    /// parser — a regex anchored on the quoted-value shape is sufficient: the interface's own
    /// <c>name: string;</c> field carries no quotes and is excluded by construction, and no comment
    /// in <c>tools.ts</c> today reproduces the quoted shape (a future one that did would fool a human
    /// skimming for <c>name: '</c> just as readily as this pattern).
    /// </summary>
    private static HashSet<string> ParseWireNames(string source) =>
        WireNamePattern.Matches(source)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(RepoPath(relativePath));

    /// <summary>
    /// Resolves a repository-relative path from this source file's compile-time location — the same
    /// idiom <see cref="MedianFigureContractTests"/> uses, rather than a second one keyed off the
    /// test binary's working directory (which differs by runner). This file lives at
    /// <c>RoselineMCP.Tests/Release/</c>, so the repository root is two levels up.
    /// </summary>
    private static string RepoPath(string relativePath, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", relativePath));
}
