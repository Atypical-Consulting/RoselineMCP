using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool listing implementations of an interface/member, overrides of a virtual/abstract member,
/// or derived types of a class — as compact summaries rather than full source.
/// </summary>
[McpServerToolType]
public static class FindImplementationsTool
{
    /// <summary>
    /// Finds implementations, overrides, or derived types for a symbol.
    /// </summary>
    [McpServerTool(Title = "Find Implementations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List the implementations of an interface/member, overrides of a virtual/abstract member, or derived types of a class — as compact summaries, instead of reading candidate files to find them. Prefer this over Grep/Read to answer 'who implements/overrides/derives from this'. Read-only: never modifies any files on disk. Limitations: capped by max (default 100); spans the loaded solution only."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit)]
    public static async Task<ToolResult<ImplementationsResponse>> FindImplementations(
        ICodeNavigationService navigationService,
        [Description("Interface, class, or member to find implementations/overrides/derived types for (simple or fully-qualified name)")]
        string symbol,
        [Description("Maximum number of results to return (default: 100)")]
        int max = 100,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(FindImplementations), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.FindImplementationsAsync(
                project, symbol, max, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<ImplementationsResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ImplementationsResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ImplementationsResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
