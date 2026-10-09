using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using RoselineMCP.Models;
using Shouldly;

namespace RoselineMCP.Tests.Models;

public class FixExamplesResponseTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    [Fact]
    public void Serializes_CamelCase_And_Omits_Null_Optionals()
    {
        var response = new FixExamplesResponse
        {
            ResolvedPath = "/x/App.sln",
            Rule = new RuleInfo { Id = "CS0168", Title = "t", Category = "Compiler", DefaultSeverity = "warning" },
            Examples = [new FixExample { File = "A.cs", Line = 3, Snippet = "s", MatchedOn = "kinds" }],
            CandidatesScanned = 4,
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(response, Wire))!.AsObject();

        json.ContainsKey("resolvedPath").ShouldBeTrue();
        json.ContainsKey("hasFixer").ShouldBeTrue();
        json.ContainsKey("candidatesScanned").ShouldBeTrue();
        json["examples"]!.AsArray().Count.ShouldBe(1);
        json.ContainsKey("alert").ShouldBeFalse();
        var rule = json["rule"]!.AsObject();
        rule.ContainsKey("description").ShouldBeFalse();
        rule.ContainsKey("helpLinkUri").ShouldBeFalse();
    }
}
