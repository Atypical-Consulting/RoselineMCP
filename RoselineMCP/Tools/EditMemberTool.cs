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
            // Gate policy lives in the helper; only the wording is this tool's own.
            //
            // The resolved target is whatever ResolveTargetPath found — the discovered .sln when
            // there is one — but this tool never writes it. CodeEditService.EditMemberAsync
            // resolves the ONE document declaring `symbol` and calls SourceTextWriter.WriteAsync
            // once, so naming the target alone claims a solution-wide write for a single-file one.
            // The qualifier says which scope is actually being authorised.
            //
            // Unlike ApplyFixes' equivalent (#149) it is UNCONDITIONAL: ApplyFixes' scope depends on
            // the target (a .csproj target *is* its whole write scope), whereas this one is a single
            // file whatever the target turns out to be — so there is no .sln branch to make.
            //
            // The one sentence holds for all three operations because 'add' resolves `symbol` as the
            // container type (AddMember rejects anything else), so "the single file declaring it"
            // names that type's declaration — the file that gets written.
            //
            // The file itself is deliberately NOT named: that costs an MSBuildWorkspace load and a
            // symbol resolution before the human has even been asked (see ResolveWriteTarget's
            // remarks), and it would reopen the window PR #142 closed — resolved once for the
            // prompt, again after a round-trip the gate allows five minutes for, with nothing
            // guaranteeing the two agree. Saying which *scope* will be written is honest about that
            // limit; naming a file that may have moved by the time it is written would not be.
            var (effectivePreviewOnly, confirmationNote, writeTarget) = await ToolExecutionHelper.ResolveWriteModeAsync(
                server,
                options,
                previewOnly,
                project,
                target => $"Write the '{operation}' of member '{symbol}' to the single file declaring it in '{target}' to disk?",
                invocation.Logger,
                cancellationToken);

            // Only NOW does the analysis budget start. Arming it before the confirmation would
            // charge the human's think-time against it — the very thing the confirmation's own
            // clock exists to prevent — and with the shipped defaults (DefaultTimeout 120s,
            // ConfirmDestructiveWritesTimeout 300s) it would already have expired, turning the
            // documented preview into a TimeoutError the caller cannot act on.
            timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

            // The path the human approved, when they were asked; otherwise the caller's own
            // argument, because nothing was resolved and so nothing was shown.
            var result = await editService.EditMemberAsync(
                writeTarget ?? project, symbol, operation, newSource, effectivePreviewOnly, timeoutSource.Token);

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
