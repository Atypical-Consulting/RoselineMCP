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
    [Description("Surgically replace, add, or delete a single C# member (method/property/field/etc.) and return a unified diff — instead of rewriting the whole file. Defaults to preview mode: with previewOnly left unset (or true), no files are changed. Pass previewOnly=false explicitly to write the change to disk.")]
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

        // Created only once the confirmation below has resolved — see the comment there.
        CancellationTokenSource? timeoutSource = null;

        try
        {
            // Whether to ask, what the answer means and what to log all live in the helper; the
            // message is the only part that is this tool's own. Note this runs BEFORE the analysis
            // budget is armed below — see the comment there.
            var (effectivePreviewOnly, confirmationNote) = await ToolExecutionHelper.ResolveWriteModeAsync(
                server,
                options,
                previewOnly,
                $"Write the '{operation}' of member '{symbol}' in '{project ?? "the auto-discovered project"}' to disk?",
                invocation.Logger,
                cancellationToken);

            // Only NOW does the analysis budget start. Arming it before the confirmation would
            // charge the human's think-time against it — the very thing the confirmation's own
            // clock exists to prevent — and with the shipped defaults (DefaultTimeout 120s,
            // ConfirmDestructiveWritesTimeout 300s) it would already have expired, turning the
            // documented preview into a TimeoutError the caller cannot act on.
            timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

            var result = await editService.EditMemberAsync(
                project, symbol, operation, newSource, effectivePreviewOnly, timeoutSource.Token);

            if (confirmationNote is not null)
            {
                result.PreviewOnly = true;
                result.Notes.Add(confirmationNote);
            }

            invocation.MarkSuccess();
            return ToolResult<EditMemberResponse>.Success(result);
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<EditMemberResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<EditMemberResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
        finally
        {
            timeoutSource?.Dispose();
        }
    }
}
