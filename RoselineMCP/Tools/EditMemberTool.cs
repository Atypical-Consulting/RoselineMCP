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

        // Each phase gets its own budget; the confirmation between them is charged to neither.
        CancellationTokenSource? timeoutSource = null;

        try
        {
            // PHASE 1 — build the candidate, diff it, and put it to the compiler. Always a preview,
            // whatever the caller asked for: nothing may reach disk before the verdict is in.
            timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);
            var result = await editService.EditMemberAsync(
                project, symbol, operation, newSource, previewOnly: true,
                allowIntroducedErrors, max, timeoutSource.Token);

            // Refused, or the caller only ever wanted a preview: either way we are done, and — this
            // is the point of the ordering — no human was asked to approve a write that was never
            // going to happen. A refusal is also strictly more informative than a decline: it
            // carries the diff *and* the errors.
            if (previewOnly || WasRefused(result, allowIntroducedErrors))
            {
                invocation.MarkSuccess();
                return ToolResult<EditMemberResponse>.Success(result);
            }

            timeoutSource.Dispose();
            timeoutSource = null;

            // Gate policy lives in the helper; only the wording is this tool's own. No analysis
            // budget is armed across it: human think-time must not be charged against the clock
            // that bounds analysis (DefaultTimeout 120s vs ConfirmDestructiveWritesTimeout 300s).
            var (effectivePreviewOnly, confirmationNote, writeTarget) = await ToolExecutionHelper.ResolveWriteModeAsync(
                server,
                options,
                previewOnly,
                project,
                target => $"Write the '{operation}' of member '{symbol}' in '{target}' to disk?",
                invocation.Logger,
                cancellationToken);

            if (effectivePreviewOnly)
            {
                // Declined or timed out. Phase 1's response already holds the diff and the verdict,
                // so there is nothing left to compute.
                result.PreviewOnly = true;
                if (confirmationNote is not null)
                {
                    result.Notes.Add(confirmationNote);
                }

                invocation.MarkSuccess();
                return ToolResult<EditMemberResponse>.Success(result);
            }

            // PHASE 2 — the approved write, on a fresh budget. It re-verifies against whatever is on
            // disk *now*: the tree may have moved while the human was deciding, and the service
            // refuses on its own if it has.
            //
            // The path the human approved, when they were asked; otherwise the caller's own
            // argument, because nothing was resolved and so nothing was shown.
            timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);
            result = await editService.EditMemberAsync(
                writeTarget ?? project, symbol, operation, newSource, previewOnly: false,
                allowIntroducedErrors, max, timeoutSource.Token);

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

    /// <summary>
    /// Whether the compile gate refused this change. Read off the verdict rather than a flag so the
    /// tool cannot disagree with the service about what "refused" means.
    /// </summary>
    internal static bool WasRefused(EditMemberResponse result, bool allowIntroducedErrors) =>
        !allowIntroducedErrors && result.Verification?.Introduced is { Count: > 0 };
}
