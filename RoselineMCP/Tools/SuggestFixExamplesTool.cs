using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool that retrieves key examples for a diagnostic no code fixer can repair.
/// </summary>
[McpServerToolType]
public static class SuggestFixExamplesTool
{
    /// <summary>
    /// Returns clean, shape-matched sites in the solution where the diagnostic does not fire.
    /// </summary>
    [McpServerTool(Title = "Suggest Fix Examples", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "How do I fix this diagnostic when apply_fixes has no fixer for it? Given a diagnostic ID, finds sites in the "
        + "same solution with the same syntactic/symbol shape where that diagnostic does NOT fire, plus the rule's title, "
        + "description and help link — evidence for writing the edit yourself. Read-only: proposes nothing, writes nothing; "
        + "edit_member/apply_fixes still compile-verify your change. "
        + "Limitations: examples are clean sites, not verified fixes; none may exist; runs the analyzers like list_diagnostics; scan bounded by maxCandidates."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit
        + " Example: suggest_fix_examples{id:'CS0121', maxExamples:3} -> rule + alert + examples[].")]
    public static async Task<ToolResult<FixExamplesResponse>> SuggestFixExamples(
        IFixExampleService fixExampleService,
        [Description("Diagnostic ID to find examples for, e.g. 'CS0121', 'CA1848', 'RCS1175'")]
        string id,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        [Description("Optional file (name or path suffix) of the occurrence to anchor on; default: the first occurrence")]
        string? file = null,
        [Description("Optional 1-based line of the occurrence to anchor on")]
        int? line = null,
        [Description("Maximum number of examples to return (default: 3)")]
        int maxExamples = 3,
        [Description("Maximum number of shape-candidate syntax nodes to scan (default: 2000)")]
        int maxCandidates = 2000,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(SuggestFixExamples), loggerFactory, server);

        if (string.IsNullOrWhiteSpace(id))
        {
            invocation.MarkFailure("validation: no id");
            return ToolExecutionHelper.ValidationError<FixExamplesResponse>(
                "Provide the diagnostic 'id' to find examples for.",
                invocation.CorrelationId,
                "Take an ID from list_diagnostics or check_compilation, e.g. id: \"CS0121\".");
        }

        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await fixExampleService.SuggestAsync(
                project, id, file, line, Math.Max(1, maxExamples), Math.Max(1, maxCandidates), timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<FixExamplesResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<FixExamplesResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<FixExamplesResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
