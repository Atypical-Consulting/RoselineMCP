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
            // Gate policy lives in the helper; only the wording is this tool's own.
            //
            // A resolved target that is a .sln needs the scope qualifier, because this tool is
            // project-scoped while the target is not: CodeFixService narrows the solution to a
            // single anchor project (ProjectLoader.SelectPrimaryProject) and fixes only that
            // project's documents. Naming the solution would have the human authorize a write
            // broader than the one about to happen — on a three-project solution, two of them are
            // left untouched and the prompt gave no hint of it.
            //
            // The anchor is deliberately NOT resolved here to name it outright: that costs an
            // MSBuildWorkspace load before the human has even been asked (see ResolveWriteTarget's
            // remarks), and it would reopen the window PR #142 closed — the target would be
            // resolved once for the prompt and again after a round-trip the gate allows five
            // minutes for, with nothing guaranteeing the two agree. Saying which *scope* will be
            // written is honest about that limit; naming the project would not be.
            var result = await ToolExecutionHelper.RunVerifiedWriteAsync(
                server,
                options,
                previewOnly,
                allowIntroducedErrors,
                project,
                target =>
                {
                    // Only the qualifier varies. Spelling the sentence out twice is how the three
                    // tools' prompts diverged before ResolveWriteModeAsync centralized them.
                    var scope = target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                        ? "the primary project of "
                        : string.Empty;
                    return $"Apply code fixes for {ids.Length} diagnostic ID(s) to {scope}'{target}' and write the changes to disk?";
                },
                (target, phasePreviewOnly, reportProgress, token) => codeFixService.ApplyFixesAsync(
                    target, ids.ToList(), phasePreviewOnly, allowIntroducedErrors, max,
                    reportProgress ? progress : null, token),
                budget,
                invocation.Logger,
                cancellationToken);

            invocation.MarkSuccess();
            return ToolResult<ApplyFixesResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ApplyFixesResponse>(cancellationToken, budget.Current, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ApplyFixesResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
