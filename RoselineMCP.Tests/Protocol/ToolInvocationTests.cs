using System.Text.Json;
using FakeItEasy;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// One JSON-RPC <c>tools/call</c> round-trip per tool, driven through a real <c>McpClient</c>
/// talking to an in-process <c>McpServer</c> (see <see cref="McpProtocolTestHost"/>). This is
/// distinct from the existing <c>Tools/AnalysisToolsTests.cs</c> suite, which invokes the static
/// tool methods as plain C# calls and never exercises argument (de)serialization, the tool-lookup
/// dispatch, or the <see cref="CallToolResult"/>/MCP error envelope produced by the SDK itself.
/// </summary>
[Collection(McpProtocolCollection.Name)]
public class ToolInvocationTests : McpProtocolTestBase
{
    [Fact]
    public async Task AnalyzeSolution_ValidCall_Returns_WellFormed_Envelope()
    {
        string? capturedPath = null;
        A.CallTo(() => AnalyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<CancellationToken>._))
            .Invokes((string path, string? _, string? _, string? _, string? _, int _, CancellationToken _) => capturedPath = path)
            .Returns(Task.FromResult(new AnalyzeSolutionResponse
            {
                Solution = "Test.sln",
                Projects = 2,
                DiagnosticSummary = new DiagnosticSummary { Error = 1 },
            }));

        var result = await Client.CallToolAsync("analyze_solution", new Dictionary<string, object?>
        {
            ["pathOrGit"] = "Test.sln",
        });

        var payload = AssertWellFormedSuccess(result);
        payload.GetProperty("solution").GetString().ShouldBe("Test.sln");
        payload.GetProperty("projects").GetInt32().ShouldBe(2);

        // Proves the argument travelled through real JSON-RPC (de)serialization, not just a
        // direct C# call.
        capturedPath.ShouldBe("Test.sln");
    }

    [Fact]
    public async Task AnalyzeSolution_Missing_Required_PathOrGit_Returns_McpLevel_Error()
    {
        var result = await Client.CallToolAsync("analyze_solution", new Dictionary<string, object?>());

        AssertMcpLevelError(result);

        A.CallTo(() => AnalyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ListDiagnostics_ValidCall_Returns_WellFormed_Envelope()
    {
        A.CallTo(() => AnalyzerService.ListDiagnosticsAsync(
                A<string>._, A<List<string>?>._, A<List<string>?>._, A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new ListDiagnosticsResponse
            {
                Project = "TestProject",
                TotalDiagnostics = 3,
                SuggestedFixableIds = ["CS0168"],
            }));

        var result = await Client.CallToolAsync("list_diagnostics", new Dictionary<string, object?>
        {
            ["project"] = "TestProject",
        });

        var payload = AssertWellFormedSuccess(result);
        payload.GetProperty("project").GetString().ShouldBe("TestProject");
        payload.GetProperty("totalDiagnostics").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task ListDiagnostics_Missing_Required_Project_Returns_McpLevel_Error()
    {
        var result = await Client.CallToolAsync("list_diagnostics", new Dictionary<string, object?>());

        AssertMcpLevelError(result);

        A.CallTo(() => AnalyzerService.ListDiagnosticsAsync(
                A<string>._, A<List<string>?>._, A<List<string>?>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ApplyFixes_ValidCall_Returns_WellFormed_Envelope()
    {
        List<string>? capturedIds = null;
        A.CallTo(() => CodeFixService.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<CancellationToken>._))
            .Invokes((string _, List<string> ids, bool _, CancellationToken _) => capturedIds = ids)
            .Returns(Task.FromResult(new ApplyFixesResponse
            {
                Project = "TestProject",
                FixedCount = 1,
                PreviewOnly = true,
            }));

        var result = await Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = "TestProject",
            ["ids"] = new[] { "RCS1213" },
        });

        var payload = AssertWellFormedSuccess(result);
        payload.GetProperty("project").GetString().ShouldBe("TestProject");
        payload.GetProperty("previewOnly").GetBoolean().ShouldBeTrue();

        capturedIds.ShouldNotBeNull();
        capturedIds.ShouldBe(["RCS1213"]);
    }

    [Fact]
    public async Task ApplyFixes_Missing_Required_Ids_Returns_McpLevel_Error()
    {
        var result = await Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = "TestProject",
        });

        AssertMcpLevelError(result);

        A.CallTo(() => CodeFixService.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A malformed (wrong JSON type, not merely absent) argument must be rejected the same
    /// MCP-level way as a missing one — no transport crash, no unhandled exception escaping the
    /// session.
    /// </summary>
    [Fact]
    public async Task ApplyFixes_Malformed_Ids_Type_Returns_McpLevel_Error()
    {
        var result = await Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = "TestProject",
            ["ids"] = 12345, // should be a string array
        });

        AssertMcpLevelError(result);
    }

    [Fact]
    public async Task CreatePatch_ValidCall_Returns_WellFormed_Envelope()
    {
        // Real PatchService/DiffService are registered (see McpProtocolTestBase) — no fake needed.
        var result = await Client.CallToolAsync("create_patch", new Dictionary<string, object?>
        {
            ["before"] = "line one",
            ["after"] = "line two",
            ["fileName"] = "sample.txt",
        });

        var payload = AssertWellFormedSuccess(result);
        payload.GetProperty("fileName").GetString().ShouldBe("sample.txt");
        payload.GetProperty("hasChanges").GetBoolean().ShouldBeTrue();
        var patch = payload.GetProperty("patch").GetString();
        patch.ShouldNotBeNullOrEmpty();
        patch.ShouldContain("sample.txt");
    }

    [Fact]
    public async Task CreatePatch_Missing_Required_After_Returns_McpLevel_Error()
    {
        var result = await Client.CallToolAsync("create_patch", new Dictionary<string, object?>
        {
            ["before"] = "only before",
        });

        AssertMcpLevelError(result);
    }

    /// <summary>
    /// Calling a tool name the server never registered is a protocol-level (JSON-RPC) failure —
    /// the SDK surfaces it to the client as a typed <see cref="McpProtocolException"/> rather than
    /// as an <c>IsError</c> <see cref="CallToolResult"/> or a crashed session. Confirms the harness
    /// (and the underlying transport) survives it and the session stays usable afterward.
    /// </summary>
    [Fact]
    public async Task Calling_Unknown_Tool_Surfaces_As_McpProtocolException_Not_A_Crash()
    {
        var ex = await Should.ThrowAsync<McpProtocolException>(
            () => Client.CallToolAsync("totally_unknown_tool", new Dictionary<string, object?>()).AsTask());

        ex.Message.ShouldContain("totally_unknown_tool");

        // The session must still be usable after a protocol-level error — proves the failure was
        // handled per-request, not by tearing down the transport/session.
        var tools = await Client.ListToolsAsync();
        tools.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Asserts the shape every successful tool response must have: not flagged as an error, and
    /// exactly one text content block whose text is well-formed JSON. Returns the parsed payload
    /// so callers can assert on individual fields.
    /// </summary>
    private static JsonElement AssertWellFormedSuccess(CallToolResult result)
    {
        result.IsError.ShouldNotBe(true);
        var block = result.Content.ShouldHaveSingleItem();
        var text = block.ShouldBeOfType<TextContentBlock>();
        text.Text.ShouldNotBeNullOrWhiteSpace();

        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    /// <summary>
    /// Asserts the MCP-level (as opposed to JSON-RPC transport-level) error shape the SDK produces
    /// when a tool invocation fails inside <c>tools/call</c> — e.g. missing/malformed arguments.
    /// This is a normal <see cref="CallToolResult"/> with <c>IsError == true</c>, not a thrown
    /// exception and not a raw transport/JSON-RPC error envelope.
    /// </summary>
    private static void AssertMcpLevelError(CallToolResult result)
    {
        result.IsError.ShouldBe(true);
        var block = result.Content.ShouldHaveSingleItem();
        var text = block.ShouldBeOfType<TextContentBlock>();
        text.Text.ShouldNotBeNullOrWhiteSpace();
    }
}
