using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for finding C# symbols by name pattern, or outlining a single file's structure —
/// a token-cheap alternative to reading whole files to discover what a project contains.
/// </summary>
[McpServerToolType]
public static class SearchSymbolsTool
{
    /// <summary>
    /// Searches a project's symbols by wildcard/substring pattern, or returns a file's outline.
    /// </summary>
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Find C# symbols (types, methods, properties, etc.) by name pattern, or outline a single file — returning compact signatures and locations instead of whole-file contents. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<SymbolSearchResponse>> SearchSymbols(
        ICodeNavigationService navigationService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Name pattern to match (substring, or wildcard with * and ?, e.g. '*Service' or 'Get*'). Omit to outline a single file via the 'file' parameter.")]
        string? query = null,
        [Description("Optional file (name or path suffix) to restrict the search to, or to outline when 'query' is omitted")]
        string? file = null,
        [Description("Optional kind filter, e.g. ['class','interface','method','property','field','enum']; also accepts 'type' and 'member'")]
        string[]? kinds = null,
        [Description("Maximum number of symbols to return (default: 50)")]
        int max = 50,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(SearchSymbols), loggerFactory);

        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(file))
        {
            invocation.MarkFailure("validation: no query or file");
            return ToolExecutionHelper.ValidationError<SymbolSearchResponse>(
                "Provide a 'query' pattern or a 'file' to outline.",
                invocation.CorrelationId,
                "Pass a name pattern (e.g. query: \"*Service\") to search, or a file (e.g. file: \"UserService.cs\") to outline its symbols.");
        }

        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.SearchSymbolsAsync(
                project, query, file, kinds, max, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<SymbolSearchResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<SymbolSearchResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<SymbolSearchResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
