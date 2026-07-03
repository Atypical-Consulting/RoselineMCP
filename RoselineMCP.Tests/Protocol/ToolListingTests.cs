using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Drives the real MCP <c>tools/list</c> negotiation (initialize handshake already completed by
/// <see cref="McpProtocolTestHost"/>) through an actual <c>McpClient</c> talking to an in-process
/// <c>McpServer</c>, and asserts the resulting tool list matches the current
/// <c>[McpServerTool]</c>-derived contract. Unlike the existing <c>Tools/*Tests.cs</c> suite (which
/// calls the static tool methods directly as plain C# calls), this exercises the JSON-RPC
/// tools/list request/response envelope and the SDK's reflection-based schema generation.
/// </summary>
[Collection(McpProtocolCollection.Name)]
public class ToolListingTests : McpProtocolTestBase
{
    private static readonly string[] ExpectedToolNames =
    [
        "analyze_solution",
        "list_diagnostics",
        "apply_fixes",
        "create_patch",
        "search_symbols",
        "get_symbol_info",
        "find_references",
        "find_implementations",
        "get_call_graph",
        "get_type_hierarchy",
        "edit_member",
        "rename_symbol",
    ];

    [Fact]
    public async Task ListTools_Returns_Exactly_The_Documented_Tools()
    {
        var tools = await Client.ListToolsAsync();

        tools.Select(t => t.Name).ShouldBe(ExpectedToolNames, ignoreOrder: true);
    }

    [Fact]
    public async Task AnalyzeSolution_Schema_Requires_Only_PathOrGit()
    {
        var tool = await GetToolAsync("analyze_solution");

        var required = GetRequired(tool);
        required.ShouldBe(["pathOrGit"]);

        var properties = GetPropertyNames(tool);
        properties.ShouldBe(
            ["pathOrGit", "branch", "include", "exclude", "severity", "maxDiagnostics"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task ListDiagnostics_Schema_Requires_Only_Project()
    {
        var tool = await GetToolAsync("list_diagnostics");

        GetRequired(tool).ShouldBe(["project"]);
        GetPropertyNames(tool).ShouldBe(["project", "ids", "files", "max"], ignoreOrder: true);
    }

    [Fact]
    public async Task ApplyFixes_Schema_Requires_Project_And_Ids()
    {
        var tool = await GetToolAsync("apply_fixes");

        GetRequired(tool).ShouldBe(["project", "ids"], ignoreOrder: true);
        GetPropertyNames(tool).ShouldBe(["project", "ids", "previewOnly"], ignoreOrder: true);
    }

    [Fact]
    public async Task CreatePatch_Schema_Requires_Before_And_After()
    {
        var tool = await GetToolAsync("create_patch");

        GetRequired(tool).ShouldBe(["before", "after"], ignoreOrder: true);
        GetPropertyNames(tool).ShouldBe(
            ["before", "after", "fileName", "ignoreWhitespace", "ignoreCase"],
            ignoreOrder: true);
    }

    /// <summary>
    /// None of the tools should expose DI-injected infrastructure parameters (services,
    /// <c>IOptions&lt;RoselineMcpOptions&gt;</c>, <c>ILoggerFactory</c>, <c>CancellationToken</c>)
    /// in their public JSON schema — those are bound automatically by the SDK from the DI
    /// container/request context, not supplied by callers.
    /// </summary>
    [Fact]
    public async Task SearchSymbols_Schema_Requires_Only_Project()
    {
        var tool = await GetToolAsync("search_symbols");

        GetRequired(tool).ShouldBe(["project"]);
        GetPropertyNames(tool).ShouldBe(
            ["project", "query", "file", "kinds", "max"], ignoreOrder: true);
    }

    [Fact]
    public async Task EditMember_Schema_Requires_Project_Symbol_And_Operation()
    {
        var tool = await GetToolAsync("edit_member");

        GetRequired(tool).ShouldBe(["project", "symbol", "operation"], ignoreOrder: true);
        GetPropertyNames(tool).ShouldBe(
            ["project", "symbol", "operation", "newSource", "previewOnly"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("analyze_solution")]
    [InlineData("list_diagnostics")]
    [InlineData("apply_fixes")]
    [InlineData("create_patch")]
    [InlineData("search_symbols")]
    [InlineData("get_symbol_info")]
    [InlineData("find_references")]
    [InlineData("find_implementations")]
    [InlineData("get_call_graph")]
    [InlineData("get_type_hierarchy")]
    [InlineData("edit_member")]
    [InlineData("rename_symbol")]
    public async Task Schema_Never_Leaks_DI_Infrastructure_Parameters(string toolName)
    {
        var tool = await GetToolAsync(toolName);

        var properties = GetPropertyNames(tool);
        properties.ShouldNotContain("cancellationToken");
        properties.ShouldNotContain("options");
        properties.ShouldNotContain("loggerFactory");
        properties.ShouldNotContain("analyzerService");
        properties.ShouldNotContain("codeFixService");
        properties.ShouldNotContain("patchService");
        properties.ShouldNotContain("navigationService");
        properties.ShouldNotContain("editService");
    }

    /// <summary>
    /// The read-only navigation/edit tools operate on a local, already-loaded project — a closed
    /// world — so they advertise <c>OpenWorldHint = false</c>. Only <c>analyze_solution</c> can
    /// reach an "open world" of external entities (it accepts a Git URL to clone), so it alone
    /// keeps <c>OpenWorldHint = true</c>.
    /// </summary>
    [Theory]
    [InlineData("analyze_solution", true)]
    [InlineData("search_symbols", false)]
    [InlineData("list_diagnostics", false)]
    [InlineData("create_patch", false)]
    [InlineData("apply_fixes", false)]
    [InlineData("rename_symbol", false)]
    public async Task Tool_OpenWorld_Hint_Reflects_Whether_The_Tool_Reaches_External_Entities(
        string toolName, bool expectedOpenWorld)
    {
        var tool = await GetToolAsync(toolName);

        tool.ProtocolTool.Annotations.ShouldNotBeNull();
        tool.ProtocolTool.Annotations.OpenWorldHint.ShouldBe(expectedOpenWorld);
    }

    private async Task<ModelContextProtocol.Client.McpClientTool> GetToolAsync(string name)
    {
        var tools = await Client.ListToolsAsync();
        return tools.Single(t => t.Name == name);
    }

    private static List<string> GetRequired(ModelContextProtocol.Client.McpClientTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        if (!schema.TryGetProperty("required", out var required))
        {
            return [];
        }

        return required.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private static List<string> GetPropertyNames(ModelContextProtocol.Client.McpClientTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        if (!schema.TryGetProperty("properties", out var properties))
        {
            return [];
        }

        return properties.EnumerateObject().Select(p => p.Name).ToList();
    }
}
