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
    [Description("Get a C# type's base-class chain, implemented interfaces, and/or derived types as compact summaries — instead of reading the declaring files. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<TypeHierarchyResponse>> GetTypeHierarchy(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Type to inspect (simple or fully-qualified name)")]
        string type,
        [Description("Which direction to report: 'base', 'derived', or 'both' (default)")]
        string direction = "both",
        [Description("Maximum number of derived types to return (default: 100)")]
        int max = 100,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(GetTypeHierarchy), loggerFactory);
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
