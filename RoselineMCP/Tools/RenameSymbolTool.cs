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
        [Description("Symbol to rename (simple or fully-qualified name)")]
        string symbol,
        [Description("New name for the symbol (must be a valid C# identifier)")]
        string newName,
        [Description("If true (the default), only preview the rename and return a diff — no files are modified. Set explicitly to false to write the changes to disk.")]
        bool previewOnly = true,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        [Description("If false (the default), a rename that introduces compiler errors — including in a downstream project the caller never named — is refused and nothing is written: the response carries the diff and the introduced errors with applied=false. Set true to write it anyway.")]
        bool allowIntroducedErrors = false,
        [Description("Maximum diagnostics returned in each verification list (default 20); the remainder are counted in verification.omitted.")]
        int max = 20,
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

        // Verify, then ask, then write — the shared flow, so the three write tools cannot drift.
        using var budget = new AnalysisBudget(cancellationToken, options);

        try
        {
            var result = await ToolExecutionHelper.RunVerifiedWriteAsync(
                server,
                options,
                previewOnly,
                allowIntroducedErrors,
                project,
                WritePrompt.ForWholeSolution(symbol, newName),
                (target, phasePreviewOnly, reportProgress, token) => editService.RenameSymbolAsync(
                    target, symbol, newName, phasePreviewOnly, allowIntroducedErrors, max,
                    reportProgress ? progress : null, token),
                budget,
                invocation.Logger,
                cancellationToken);

            invocation.MarkSuccess();
            return ToolResult<RenameSymbolResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<RenameSymbolResponse>(cancellationToken, budget.Current, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<RenameSymbolResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
