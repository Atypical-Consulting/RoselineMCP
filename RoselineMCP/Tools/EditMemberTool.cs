using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for surgical, member-level edits (replace/add/delete). Emitting a single member instead
/// of a whole-file rewrite keeps the tokens an agent must produce proportional to the change.
/// </summary>
[McpServerToolType]
public static class EditMemberTool
{
    private static readonly HashSet<string> ValidOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "replace", "add", "delete"
    };

    /// <summary>
    /// Replaces, adds, or deletes a single type member and returns a unified diff.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ApplyFixesTool"/>, this defaults to preview mode (<c>previewOnly: true</c>) so
    /// nothing is written to disk unless the caller explicitly passes <c>previewOnly: false</c>. The
    /// <see cref="McpServerToolAttribute.Destructive"/> hint is a static worst-case annotation: the
    /// tool *can* write a file when preview mode is turned off.
    /// </remarks>
    [McpServerTool(Title = "Edit Member", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Surgically replace, add, or delete a single C# member (method/property/field/etc.) and return a unified diff — instead of rewriting the whole file. Defaults to preview mode: with previewOnly left unset (or true), no files are changed. Pass previewOnly=false explicitly to write the change to disk. Limitations: one member at a time; refused if the change introduces compiler errors unless allowIntroducedErrors=true."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit)]
    public static async Task<ToolResult<EditMemberResponse>> EditMember(
        ICodeEditService editService,
        [Description("For 'replace'/'delete': the member to edit. For 'add': the container type to add a member to. Simple or fully-qualified name.")]
        string symbol,
        [Description("Operation to perform: 'replace', 'add', or 'delete'")]
        string operation,
        [Description("New C# member declaration source (required for 'replace' and 'add'; ignored for 'delete')")]
        string? newSource = null,
        [Description("If true (the default), only preview the change and return a diff — no files are modified. Set explicitly to false to write the change to disk.")]
        bool previewOnly = true,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        [Description("If false (the default), an edit that introduces compiler errors is refused and nothing is written: the response carries the diff and the introduced errors with applied=false. Set true to write it anyway.")]
        bool allowIntroducedErrors = false,
        [Description("Maximum diagnostics returned in each verification list (default 20); the remainder are counted in verification.omitted.")]
        int max = 20,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(EditMember), loggerFactory, server);

        if (string.IsNullOrWhiteSpace(operation) || !ValidOperations.Contains(operation))
        {
            invocation.MarkFailure("validation: invalid operation");
            return ToolExecutionHelper.ValidationError<EditMemberResponse>(
                $"Invalid or missing operation '{operation}'.",
                invocation.CorrelationId,
                "Valid operations are: replace, add, delete.");
        }

        // Verify, then ask, then write — the shared flow, so the three write tools cannot drift.
        using var budget = new AnalysisBudget(cancellationToken, options);

        try
        {
            // Gate policy AND wording both live in the helper now: this tool names its scope and
            // hands over the values, and WritePrompt.Render composes the sentence (#161). The
            // reasoning for the SingleFile clause — why it says "exactly one file is rewritten",
            // why it says "loaded from" rather than "in", and why 'add' names the container type
            // instead of a member — moved there with it, along with the sanitising of `symbol`.
            var result = await ToolExecutionHelper.RunVerifiedWriteAsync(
                server,
                options,
                previewOnly,
                allowIntroducedErrors,
                project,
                WritePrompt.ForSingleFile(operation, symbol),
                (target, phasePreviewOnly, _, token) => editService.EditMemberAsync(
                    target, symbol, operation, newSource, phasePreviewOnly, allowIntroducedErrors, max, token),
                budget,
                invocation.Logger,
                cancellationToken);

            invocation.MarkSuccess();
            return ToolResult<EditMemberResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<EditMemberResponse>(cancellationToken, budget.Current, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<EditMemberResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
