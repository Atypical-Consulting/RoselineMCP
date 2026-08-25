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
    [Description("Apply code fixes for specified diagnostic IDs in a project. Defaults to preview mode: with previewOnly left unset (or true), no files are changed and only a diff is returned. Pass previewOnly=false explicitly to write the fixes to disk. Limitations: IDs with no registered fixer are reported, not fixed; nothing is written unless previewOnly=false."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit)]
    public static async Task<ToolResult<ApplyFixesResponse>> ApplyFixes(
        ICodeFixService codeFixService,
        [Description("List of diagnostic IDs to fix (e.g., ['RCS1213', 'SA1101'])")]
        string[] ids,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        [Description("If true (the default), only preview changes and return a diff — no files are modified. Set explicitly to false to apply the fixes and write changes to disk.")]
        bool previewOnly = true,
        [Description("If false (the default), fixes whose result introduces compiler errors are refused and nothing is written: the response carries the patch and the introduced errors with applied=false. Set true to write them anyway.")]
        bool allowIntroducedErrors = false,
        [Description("Maximum diagnostics returned in each verification list (default 20); the remainder are counted in verification.omitted.")]
        int max = 20,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        IProgress<ProgressNotificationValue>? progress = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(ApplyFixes), loggerFactory, server);

        if (ids == null || ids.Length == 0)
        {
            invocation.MarkFailure("validation: no diagnostic IDs provided");
            return ToolExecutionHelper.ValidationError<ApplyFixesResponse>(
                "No diagnostic IDs provided.",
                invocation.CorrelationId,
                "Call ListDiagnostics first to discover fixable diagnostic IDs for this project, then pass one or more of them, e.g. ids: [\"RCS1213\"].");
        }

        // Verify, then ask, then write — the shared flow, so the three write tools cannot drift.
        using var budget = new AnalysisBudget(cancellationToken, options);

        try
        {
            // Gate policy AND wording both live in the helper now: this tool names its scope and
            // hands over the values, and WritePrompt.Render composes the sentence (#161). The
            // reasoning for the PrimaryProjectOf clause — why a .sln target needs the qualifier and
            // why the anchor project is deliberately not resolved to be named — moved there with it.
            var result = await ToolExecutionHelper.RunVerifiedWriteAsync(
                server,
                options,
                previewOnly,
                allowIntroducedErrors,
                project,
                WritePrompt.ForPrimaryProjectOf(ids.Length),
                (target, phasePreviewOnly, reportProgress, token) => codeFixService.ApplyFixesAsync(
                    target, ids.ToList(), phasePreviewOnly, allowIntroducedErrors, max,
                    reportProgress ? progress : null, token),
                budget,
                invocation.Logger,
                cancellationToken);

            invocation.MarkSuccess();
            return ToolResult<ApplyFixesResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ApplyFixesResponse>(cancellationToken, budget.Current, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ApplyFixesResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
