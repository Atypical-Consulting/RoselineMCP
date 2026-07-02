using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for creating unified diff patches between two text blobs.
/// </summary>
[McpServerToolType]
public static class CreatePatchTool
{
    /// <summary>
    /// Creates a unified diff patch between two text blobs.
    /// </summary>
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Create a unified diff patch between two text blobs. Read-only: operates purely on the provided strings and never touches the filesystem.")]
    public static string CreatePatch(
        IPatchService patchService,
        [Description("The original text content (before changes)")]
        string before,
        [Description("The modified text content (after changes)")]
        string after,
        [Description("Optional file name for the patch header (default: 'file.txt')")]
        string? fileName = null,
        [Description("If true, ignore whitespace-only differences (trailing spaces, run-length) when computing the diff (default: false)")]
        bool ignoreWhitespace = false,
        [Description("If true, ignore case differences when computing the diff (default: false)")]
        bool ignoreCase = false,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(CreatePatch), loggerFactory);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = patchService.CreatePatchWithOptions(
                before,
                after,
                fileName,
                ignoreWhitespace: ignoreWhitespace,
                ignoreCase: ignoreCase,
                cancellationToken: timeoutSource.Token);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });

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