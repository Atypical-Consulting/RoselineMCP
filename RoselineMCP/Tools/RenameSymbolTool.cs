using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for a Roslyn-driven, solution-wide rename that updates every reference and returns a
/// unified diff — far cheaper in emitted tokens than rewriting each affected file.
/// </summary>
[McpServerToolType]
public static class RenameSymbolTool
{
    /// <summary>
    /// Renames a symbol across the solution and returns a unified diff.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ApplyFixesTool"/>, this defaults to preview mode (<c>previewOnly: true</c>) so
    /// nothing is written to disk unless the caller explicitly passes <c>previewOnly: false</c>. The
    /// <see cref="McpServerToolAttribute.Destructive"/> hint is a static worst-case annotation: the
    /// tool *can* write files when preview mode is turned off.
    /// </remarks>
    [McpServerTool(Title = "Rename Symbol", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Rename a C# symbol and update every reference across the solution using Roslyn, returning a unified diff. Defaults to preview mode: with previewOnly left unset (or true), no files are changed. Pass previewOnly=false explicitly to write the changes to disk.")]
    public static async Task<ToolResult<RenameSymbolResponse>> RenameSymbol(
        ICodeEditService editService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Symbol to rename (simple or fully-qualified name)")]
        string symbol,
        [Description("New name for the symbol (must be a valid C# identifier)")]
        string newName,
        [Description("If true (the default), only preview the rename and return a diff — no files are modified. Set explicitly to false to write the changes to disk.")]
        bool previewOnly = true,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        IProgress<ProgressNotificationValue>? progress = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(RenameSymbol), loggerFactory, server);

        if (string.IsNullOrWhiteSpace(newName))
        {
            invocation.MarkFailure("validation: missing newName");
            return ToolExecutionHelper.ValidationError<RenameSymbolResponse>(
                "No new name provided.",
                invocation.CorrelationId,
                "Pass a valid C# identifier as newName, e.g. newName: \"GetUserById\".");
        }

        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var effectivePreviewOnly = previewOnly;
            string? declineNote = null;
            // Use the caller's request token (not the wall-clock timeout) for the human confirmation
            // round-trip: think-time must not be charged against the analysis budget.
            if (!previewOnly && !await ToolExecutionHelper.ConfirmDestructiveWriteAsync(
                    server,
                    $"Rename '{symbol}' to '{newName}' across the solution and write the changes to disk?",
                    cancellationToken))
            {
                effectivePreviewOnly = true;
                declineNote = "Write declined via client confirmation; returned a preview only (no files were modified).";
            }

            var result = await editService.RenameSymbolAsync(
                project, symbol, newName, effectivePreviewOnly, progress, timeoutSource.Token);

            if (declineNote is not null)
            {
                result.PreviewOnly = true;
                result.Notes.Add(declineNote);
            }

            invocation.MarkSuccess();
            return ToolResult<RenameSymbolResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<RenameSymbolResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<RenameSymbolResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
