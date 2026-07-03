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
/// MCP tool for applying code fixes for specified diagnostic IDs in a project.
/// </summary>
[McpServerToolType]
public static class ApplyFixesTool
{
    /// <summary>
    /// Applies code fixes for specified diagnostic IDs in a project.
    /// </summary>
    /// <remarks>
    /// Defaults to preview mode (<c>previewOnly: true</c>) so calling this tool without
    /// specifying <paramref name="previewOnly"/> never writes to disk — the caller must opt in
    /// to writing by passing <c>previewOnly: false</c> explicitly. This keeps the tool's actual
    /// behavior aligned with the "Read-Only by Default" guarantee documented in README.md.
    /// The <see cref="McpServerToolAttribute.Destructive"/> hint is a static, worst-case
    /// annotation: it is <see langword="true"/> because the tool *can* write files when
    /// <paramref name="previewOnly"/> is explicitly set to <see langword="false"/>, even though
    /// the default call is non-destructive. The current MCP SDK annotation model has no way to
    /// express "destructive only for a specific parameter value".
    /// </remarks>
    [McpServerTool(Title = "Apply Fixes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Apply code fixes for specified diagnostic IDs in a project. Defaults to preview mode: with previewOnly left unset (or true), no files are changed and only a diff is returned. Pass previewOnly=false explicitly to write the fixes to disk.")]
    public static async Task<ToolResult<ApplyFixesResponse>> ApplyFixes(
        ICodeFixService codeFixService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("List of diagnostic IDs to fix (e.g., ['RCS1213', 'SA1101'])")]
        string[] ids,
        [Description("If true (the default), only preview changes and return a diff — no files are modified. Set explicitly to false to apply the fixes and write changes to disk.")]
        bool previewOnly = true,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(ApplyFixes), loggerFactory);

        if (ids == null || ids.Length == 0)
        {
            invocation.MarkFailure("validation: no diagnostic IDs provided");
            return ToolExecutionHelper.ValidationError<ApplyFixesResponse>(
                "No diagnostic IDs provided.",
                invocation.CorrelationId,
                "Call ListDiagnostics first to discover fixable diagnostic IDs for this project, then pass one or more of them, e.g. ids: [\"RCS1213\"].");
        }

        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await codeFixService.ApplyFixesAsync(
                project,
                ids.ToList(),
                previewOnly,
                progress,
                timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<ApplyFixesResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ApplyFixesResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ApplyFixesResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
