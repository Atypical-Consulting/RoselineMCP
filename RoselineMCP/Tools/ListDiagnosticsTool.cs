using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for listing detailed diagnostics for a specific project.
/// </summary>
[McpServerToolType]
public static class ListDiagnosticsTool
{
    /// <summary>
    /// Lists detailed diagnostics for a specific project with statistics and fixable suggestions.
    /// </summary>
    [McpServerTool(Title = "List Diagnostics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "What should I clean up here? A broad inventory of a project's diagnostics — compiler AND analyzer "
        + "(Roslynator RCS*, StyleCop, the project's own analyzer references) — with per-ID and per-severity "
        + "statistics and which IDs apply_fixes can fix. Read-only: never modifies any files on disk. "
        + "Running the analyzers costs several times a bare compile, so this is the exploratory tool, not the "
        + "edit-loop one. To ask \"is it still building?\" after an edit — compiler errors only, in under a "
        + "second — use check_compilation instead. Limitations: runs third-party analyzers in-process; RoselineMCP:RunAnalyzers=false makes it compiler-only."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit
        + " Example: list_diagnostics{ids:['CS0168'], max:50} -> resolvedPath + diagnostics[] + per-ID/severity stats.")]
    public static async Task<ToolResult<ListDiagnosticsResponse>> ListDiagnostics(
        ISolutionAnalyzerService analyzerService,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        [Description("Optional list of diagnostic IDs to filter (e.g., ['CS0168', 'CS0219'])")]
        string[]? ids = null,
        [Description("Optional list of file patterns to filter (e.g., ['Controller.cs', 'Service.cs'])")]
        string[]? files = null,
        [Description("Maximum number of diagnostic details to return (default: 100)")]
        int max = 100,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(ListDiagnostics), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await analyzerService.ListDiagnosticsAsync(
                project,
                ids?.ToList(),
                files?.ToList(),
                max,
                timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<ListDiagnosticsResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<ListDiagnosticsResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<ListDiagnosticsResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
