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
    [Description("List every use site of a C# symbol across the whole solution (file/line + a one-line snippet each) — instead of grepping and opening every referencing file. Prefer this over Grep/Read to answer 'where is this used'. Read-only: never modifies any files on disk. Limitations: capped by max (default 100); finds source references only, not reflection or string-based use."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit)]
    public static async Task<ToolResult<ReferencesResponse>> FindReferences(
        ICodeNavigationService navigationService,
        [Description("Symbol to find references for: a simple name or a fully-qualified name to disambiguate")]
        string symbol,
        [Description("If true, also include the symbol's own declaration among the results (default: false)")]
        bool includeDefinition = false,
        [Description("Maximum number of reference locations to return (default: 100)")]
        int max = 100,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
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
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ReferencesResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ReferencesResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
