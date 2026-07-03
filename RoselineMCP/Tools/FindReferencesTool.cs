using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool listing every reference (use site) of a symbol across the solution as location + snippet,
/// so an agent can survey usage without opening each referencing file.
/// </summary>
[McpServerToolType]
public static class FindReferencesTool
{
    /// <summary>
    /// Finds all references to a symbol across the solution.
    /// </summary>
    [McpServerTool(Title = "Find References", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find every reference (use site) of a C# symbol across the solution, each as a file/line/column plus a one-line snippet — instead of reading the referencing files. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<ReferencesResponse>> FindReferences(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Symbol to find references for: a simple name or a fully-qualified name to disambiguate")]
        string symbol,
        [Description("If true, also include the symbol's own declaration among the results (default: false)")]
        bool includeDefinition = false,
        [Description("Maximum number of reference locations to return (default: 100)")]
        int max = 100,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(FindReferences), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.FindReferencesAsync(
                project, symbol, includeDefinition, max, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<ReferencesResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ReferencesResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ReferencesResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
