using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool building a depth-bounded caller/callee graph for a method, so an agent can trace control
/// flow without reading method bodies.
/// </summary>
[McpServerToolType]
public static class GetCallGraphTool
{
    /// <summary>
    /// Builds a caller and/or callee graph for a method with cycle detection.
    /// </summary>
    [McpServerTool(Title = "Get Call Graph", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Build a depth-bounded caller and/or callee graph for a C# method (with cycle detection) to trace control flow without reading method bodies. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<CallGraphResponse>> GetCallGraph(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Method to build the graph around (simple or fully-qualified name)")]
        string method,
        [Description("Traversal direction: 'callers' (default), 'callees', or 'both'")]
        string direction = "callers",
        [Description("Traversal depth, 1-3 (default: 1)")]
        int depth = 1,
        [Description("Maximum number of nodes to expand per direction (default: 50)")]
        int max = 50,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(GetCallGraph), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.GetCallGraphAsync(
                project, method, direction, depth, max, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<CallGraphResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<CallGraphResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<CallGraphResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
