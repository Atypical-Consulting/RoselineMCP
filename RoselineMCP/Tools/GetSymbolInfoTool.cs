using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
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
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Get a C# symbol's kind, accessibility, modifiers, signature, base types, interfaces, XML docs, and definition location (optionally its source) — instead of reading the whole file. Read-only: never modifies any files on disk.")]
    public static async Task<string> GetSymbolInfo(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Symbol to describe: a simple name (e.g. 'UserService') or a fully-qualified name (e.g. 'Acme.Users.UserService.GetUser') to disambiguate")]
        string symbol,
        [Description("If true (the default), include the exact source text of the symbol's declaration")]
        bool includeSource = true,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(GetSymbolInfo), loggerFactory);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.GetSymbolInfoAsync(
                project, symbol, includeSource, timeoutSource.Token);

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
