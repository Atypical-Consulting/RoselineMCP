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
    [Description("Look up one C# symbol's kind, modifiers, signature, base types/interfaces, XML docs, and definition location — the token-cheap 'go to definition'. Prefer this over reading the whole file; pass includeSource:true to get the member's exact body instead of Read. Read-only: never modifies any files on disk. Limitations: a simple name can match several symbols — pass a fully-qualified name to disambiguate."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit)]
    public static async Task<ToolResult<SymbolInfoResponse>> GetSymbolInfo(
        ICodeNavigationService navigationService,
        [Description("Symbol to describe: a simple name (e.g. 'UserService') or a fully-qualified name (e.g. 'Acme.Users.UserService.GetUser') to disambiguate")]
        string symbol,
        [Description("If true (the default), include the exact source text of the symbol's declaration")]
        bool includeSource = true,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
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
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<SymbolInfoResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<SymbolInfoResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
