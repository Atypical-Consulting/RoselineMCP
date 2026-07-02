namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Forces every MCP protocol test class to run sequentially relative to one another (never
/// concurrently within this collection). Only these classes go through
/// <c>AddMcpServer().WithToolsFromAssembly()</c> / <c>AIFunctionFactory.Create</c> for the shared,
/// static <c>[McpServerTool]</c> methods on <c>RoselineMCP.Tools.*</c>.
/// </summary>
/// <remarks>
/// <c>Microsoft.Extensions.AI.AIFunctionFactory</c> memoizes the reflection-derived tool
/// descriptor (which parameters end up in the JSON schema, including whether a DI-bound
/// parameter such as <c>ICodeFixService</c> or <c>IOptions&lt;RoselineMcpOptions&gt;</c> is
/// excluded) in a process-wide <c>ConditionalWeakTable</c> keyed off the SDK's shared
/// <c>McpJsonUtilities.DefaultOptions</c> <see cref="System.Text.Json.JsonSerializerOptions"/>
/// instance — not per <see cref="McpProtocolTestHost"/>/DI container. Building two
/// <see cref="McpProtocolTestHost"/> instances concurrently (the default xUnit behavior — each
/// test class is its own parallel collection) races on that shared cache: whichever host resolves
/// a given tool's schema first can nondeterministically "poison" it for every other host in the
/// process for the rest of the run, intermittently leaking DI-only parameters
/// (<c>codeFixService</c>, <c>loggerFactory</c>, <c>options</c>) into the public tool schema.
/// Serializing these tests via this collection (instead of disabling parallelism assembly-wide)
/// avoids that race without slowing down the rest of the suite, which never exercises this
/// reflection-based registration path.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class McpProtocolCollection
{
    public const string Name = "MCP Protocol (sequential — shared AIFunctionFactory schema cache)";
}
