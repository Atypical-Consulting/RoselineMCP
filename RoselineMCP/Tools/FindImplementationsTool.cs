using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
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
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Find implementations of an interface or interface member, overrides of a virtual/abstract member, or derived types of a class — as compact symbol summaries. Read-only: never modifies any files on disk.")]
    public static async Task<string> FindImplementations(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Interface, class, or member to find implementations/overrides/derived types for (simple or fully-qualified name)")]
        string symbol,
        [Description("Maximum number of results to return (default: 100)")]
        int max = 100,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(FindImplementations), loggerFactory);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.FindImplementationsAsync(
                project, symbol, max, timeoutSource.Token);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            invocation.MarkSuccess();
            return json;
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.SerializeCancellation(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.SerializeError(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
