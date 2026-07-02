using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Golden-file guard on the JSON schema the MCP SDK generates (via
/// <c>WithToolsFromAssembly()</c>'s reflection over each <c>[McpServerTool]</c> method) for all
/// four tools. This is deliberately narrow — it snapshots only <c>InputSchema</c> (parameter
/// names, types, and which are required), not free-text like descriptions — so it fails exactly
/// when a tool's *contract* changes (a parameter renamed, retyped, added, removed, or moved
/// between required/optional), which is precisely the kind of silent break a caller integrating
/// against this server would otherwise only discover at runtime (e.g. the historical
/// <c>includePattern</c> → <c>include</c> rename on <c>AnalyzeSolution</c>).
/// </summary>
/// <remarks>
/// To intentionally accept a schema change, regenerate the snapshot by running this test once
/// with the <c>ROSELINE_UPDATE_SCHEMA_SNAPSHOT=1</c> environment variable set, then review the
/// resulting diff to <c>Snapshots/tool-schemas.snapshot.json</c> like any other code change.
/// </remarks>
[Collection(McpProtocolCollection.Name)]
public class ToolSchemaSnapshotTests : McpProtocolTestBase
{
    [Fact]
    public async Task Tool_Input_Schemas_Match_Committed_Snapshot()
    {
        var tools = await Client.ListToolsAsync();

        var actual = new JsonObject();
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            actual[tool.Name] = Canonicalize(JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()));
        }

        var actualJson = actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (Environment.GetEnvironmentVariable("ROSELINE_UPDATE_SCHEMA_SNAPSHOT") == "1")
        {
            File.WriteAllText(SnapshotPath(), actualJson + Environment.NewLine);
        }

        File.Exists(SnapshotPath()).ShouldBeTrue(
            $"No committed schema snapshot found at '{SnapshotPath()}'. " +
            "Generate one by running this test with ROSELINE_UPDATE_SCHEMA_SNAPSHOT=1 set, then commit the file.");

        var expectedJson = File.ReadAllText(SnapshotPath()).TrimEnd('\r', '\n');

        actualJson.ShouldBe(
            expectedJson,
            "The tool input JSON schema no longer matches the committed snapshot at " +
            $"'{SnapshotPath()}'. If this is an intentional contract change, regenerate the " +
            "snapshot (ROSELINE_UPDATE_SCHEMA_SNAPSHOT=1) and review the diff before committing it.");
    }

    /// <summary>Recursively sorts JSON object keys so the comparison is stable regardless of the
    /// SDK's reflection enumeration order, while leaving array element order (e.g. "required")
    /// intact since it can be meaningful.</summary>
    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    sorted[key] = Canonicalize(obj[key]?.DeepClone());
                }
                return sorted;
            case JsonArray arr:
                var canonicalArray = new JsonArray();
                foreach (var item in arr)
                {
                    canonicalArray.Add(Canonicalize(item?.DeepClone()));
                }
                return canonicalArray;
            default:
                return node?.DeepClone();
        }
    }

    private static string SnapshotPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", "tool-schemas.snapshot.json");
}
