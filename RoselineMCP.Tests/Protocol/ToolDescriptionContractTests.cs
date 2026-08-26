using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using RoselineMCP.Tests.Support;
using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Pins the six-component contract on every <c>[McpServerTool]</c> description (arXiv:2602.14878):
/// Purpose, Guidelines and Parameter explanation are reviewed by eye; Limitations, Examples and the
/// compactness ceiling are mechanical. Discovery is by reflection over the server assembly, so a
/// <em>new</em> tool inherits the contract instead of relying on someone remembering it.
/// </summary>
public class ToolDescriptionContractTests
{
    /// <summary>
    /// The compactness ceiling. arXiv:2602.14878 measured full six-component enrichment at
    /// +67.46% steps and a 16.67% regression rate, while compact variants preserved the reliability
    /// without the overhead — so the budget is deliberately tight. Raising it is allowed, but only
    /// as a visible, reviewed edit to this constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set from the measured worst case, not from taste. Issue #179 proposed 120 on the premise
    /// that it "admits both additions to the longest tool"; that arithmetic does not hold. The
    /// longest baseline description (<c>check_compilation</c>) was already <b>98</b> words before
    /// this change, and the two mandated components cost 62 more — its own <c>Limitations:</c>
    /// clause (13), <see cref="RoselineToolDescriptions.ProjectAutoDiscoveryLimit"/> (39) and an
    /// <c>Example:</c> line (10) — landing at <b>160</b>. Reaching 120 would have required cutting
    /// existing Purpose/Guidelines text, which #179 forbids outright and which is this repo's
    /// measured strength (the ecosystem fails that component 89.3% of the time).
    /// </para>
    /// <para>
    /// So 165 = the 160-word worst case plus a small margin: still far below the "enrich
    /// everything" regime the paper priced, and still tight enough that no tool can absorb a
    /// paragraph of prose. If a future tool needs more, raise this deliberately — and say why.
    /// </para>
    /// </remarks>
    private const int MaxWords = 165;

    /// <summary>
    /// Every <c>[McpServerTool]</c> method in the server assembly, as
    /// <c>(methodName, description, hasOptionalProject)</c>. Reflected once, in
    /// <see cref="ReflectedTools"/>, so this contract and <c>WebsiteToolsDataContractTests</c>'
    /// pin of <c>website/src/data/tools.ts</c> can never disagree about what a tool is (#208).
    /// </summary>
    public static IEnumerable<object[]> ToolDescriptions() =>
        ReflectedTools.Methods()
            .Select(m => new object[]
            {
                m.Name,
                m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                m.GetParameters().Any(p => p.Name == "project" && p.IsOptional),
            });

    [Fact]
    public void Every_Tool_Is_Covered_By_The_Contract()
        => ToolDescriptions().Count().ShouldBe(14,
            "A tool was added or removed. Give the new tool a Limitations clause and an Example, " +
            "then update this count deliberately.");

    [Theory]
    [MemberData(nameof(ToolDescriptions))]
    public void Description_Stays_Compact(string name, string description, bool hasOptionalProject)
    {
        _ = hasOptionalProject;

        var words = description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        words.ShouldBeLessThanOrEqualTo(MaxWords,
            $"{name}'s description is {words} words. arXiv:2602.14878 measured full six-component " +
            "enrichment at +67.46% steps and a 16.67% regression rate; compact variants kept the " +
            "reliability without the overhead. Trim, or raise MaxWords deliberately in review.");
    }

    /// <summary>
    /// The count of fragment-carrying tools, pinned because five prose surfaces say "twelve"
    /// (README, <c>docs/API.md</c> ×2, the constant's own docs and a failure message below) and
    /// nothing else would notice them going stale. <see cref="Optional_Project_Tools_Carry_The_Shared_Limitation"/>
    /// returns early for a tool without an optional <c>project</c>, so on its own it can never
    /// observe that the population changed size.
    /// </summary>
    [Fact]
    public void Exactly_Twelve_Tools_Take_An_Optional_Project()
        => ToolDescriptions().Count(t => (bool)t[2]).ShouldBe(12,
            "The number of tools with an optional 'project' changed. Update the count here AND the " +
            "prose that says \"twelve\": README.md, docs/API.md (twice) and " +
            "RoselineToolDescriptions.ProjectAutoDiscoveryLimit's XML docs.");

    [Theory]
    [MemberData(nameof(ToolDescriptions))]
    public void Every_Description_States_Its_Limitations(
        string name, string description, bool hasOptionalProject)
    {
        _ = hasOptionalProject;

        description.ShouldContain("Limitations:", Case.Sensitive,
            $"{name} states no Limitations. arXiv:2602.14878 found Unstated Limitations in 89.8% of " +
            "856 MCP tools; one sentence naming the failure mode a caller cannot infer is enough.");
    }

    [Theory]
    [MemberData(nameof(ToolDescriptions))]
    public void Every_Description_Shows_One_Example(
        string name, string description, bool hasOptionalProject)
    {
        _ = hasOptionalProject;

        description.ShouldContain("Example:", Case.Sensitive,
            $"{name} shows no example call. One line — tool{{arg:'value'}} -> what comes back.");

        description.Split("Example:").Length.ShouldBe(2,
            $"{name} has more than one Example: block. One is the budget.");
    }

    [Theory]
    [MemberData(nameof(ToolDescriptions))]
    public void Optional_Project_Tools_Carry_The_Shared_Limitation(
        string name, string description, bool hasOptionalProject)
    {
        if (!hasOptionalProject)
        {
            return;
        }

        description.ShouldContain(RoselineToolDescriptions.ProjectAutoDiscoveryLimit.Trim(),
            Case.Sensitive,
            $"{name} takes an optional 'project' but does not state that auto-discovery is anchored " +
            "to the SERVER's cwd. Append RoselineToolDescriptions.ProjectAutoDiscoveryLimit — verbatim, " +
            "not a paraphrase, so all twelve stay in sync.");
    }
}
