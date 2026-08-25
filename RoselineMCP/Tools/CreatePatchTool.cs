using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
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
    [McpServerTool(Title = "Create Patch", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create a unified diff patch between two text blobs. Read-only: operates purely on the provided strings and never touches the filesystem. Limitations: a pure string diff — it never reads or writes files, and fileName only labels the patch header. Example: create_patch{before:'a', after:'b', fileName:'x.cs'} -> unified diff + line counts.")]
    public static ToolResult<CreatePatchResponse> CreatePatch(
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
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(CreatePatch), loggerFactory, server);
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

            invocation.MarkSuccess();
            return ToolResult<CreatePatchResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<CreatePatchResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<CreatePatchResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
