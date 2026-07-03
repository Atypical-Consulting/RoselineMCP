using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool returning a single symbol's declaration metadata, signature, and definition — the
/// token-cheap substitute for reading a whole file to "go to definition".
/// </summary>
[McpServerToolType]
public static class GetSymbolInfoTool
{
    /// <summary>
    /// Returns declaration details (kind, accessibility, modifiers, signature, base types,
    /// interfaces, docs, definition location and optional source) for a symbol.
    /// </summary>
    [McpServerTool(Title = "Get Symbol Info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get a C# symbol's kind, accessibility, modifiers, signature, base types, interfaces, XML docs, and definition location (optionally its source) — instead of reading the whole file. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<SymbolInfoResponse>> GetSymbolInfo(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Symbol to describe: a simple name (e.g. 'UserService') or a fully-qualified name (e.g. 'Acme.Users.UserService.GetUser') to disambiguate")]
        string symbol,
        [Description("If true (the default), include the exact source text of the symbol's declaration")]
        bool includeSource = true,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(GetSymbolInfo), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.GetSymbolInfoAsync(
                project, symbol, includeSource, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<SymbolInfoResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<SymbolInfoResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<SymbolInfoResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
