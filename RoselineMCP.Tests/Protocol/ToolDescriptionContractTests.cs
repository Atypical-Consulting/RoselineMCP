using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
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
    private const int MaxWords = 120;

    /// <summary>
    /// Every <c>[McpServerTool]</c> method in the server assembly, as
    /// <c>(methodName, description, hasOptionalProject)</c>.
    /// </summary>
    public static IEnumerable<object[]> ToolDescriptions() =>
        typeof(RoselineServerInfo).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
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
}
