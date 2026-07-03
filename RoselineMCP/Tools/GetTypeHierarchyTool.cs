using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool returning a type's base-class chain, implemented interfaces, and derived types as compact
/// summaries — the structural relationships without the declaring files.
/// </summary>
[McpServerToolType]
public static class GetTypeHierarchyTool
{
    /// <summary>
    /// Returns base types, interfaces, and/or derived types for a type.
    /// </summary>
    [McpServerTool(Title = "Get Type Hierarchy", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Show a C# type's base-class chain, implemented interfaces, and/or derived types as compact summaries — instead of reading the declaring files. Prefer this over Read/Grep to answer 'what does this inherit / who derives from it'. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<TypeHierarchyResponse>> GetTypeHierarchy(
        ICodeNavigationService navigationService,
        [Description("Type to inspect (simple or fully-qualified name)")]
        string type,
        [Description("Which direction to report: 'base', 'derived', or 'both' (default)")]
        string direction = "both",
        [Description("Maximum number of derived types to return (default: 100)")]
        int max = 100,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(GetTypeHierarchy), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.GetTypeHierarchyAsync(
                project, type, direction, max, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<TypeHierarchyResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<TypeHierarchyResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<TypeHierarchyResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
