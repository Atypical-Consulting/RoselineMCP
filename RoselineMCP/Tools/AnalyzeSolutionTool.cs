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
/// MCP tool for analyzing C# solutions and returning diagnostics summary.
/// </summary>
[McpServerToolType]
public static class AnalyzeSolutionTool
{
    private static readonly HashSet<string> ValidSeverities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Error", "Warning", "Info", "Hidden"
    };

    /// <summary>
    /// Analyzes a C# solution and returns diagnostics summary with details about errors, warnings, and info messages.
    /// </summary>
    [McpServerTool(Title = "Analyze Solution", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Analyze a C# solution and return diagnostics summary with details about errors, warnings, and info messages. Read-only: never modifies any files on disk.")]
    public static async Task<ToolResult<AnalyzeSolutionResponse>> AnalyzeSolution(
        ISolutionAnalyzerService analyzerService,
        [Description("Path to solution file or directory containing .sln file, or Git repository URL")]
        string pathOrGit,
        [Description("Git branch name (only used if pathOrGit is a Git URL)")]
        string? branch = null,
        [Description("Include pattern for project names (e.g., 'Core' to only analyze projects containing 'Core')")]
        string? include = null,
        [Description("Exclude pattern for project names (e.g., 'Test' to skip test projects)")]
        string? exclude = null,
        [Description("Minimum severity level to include: Error, Warning, Info, or Hidden")]
        string? severity = null,
        [Description("Maximum number of diagnostics to return (default: 100)")]
        int maxDiagnostics = 100,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        IProgress<ProgressNotificationValue>? progress = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(AnalyzeSolution), loggerFactory, server);

        if (!string.IsNullOrEmpty(severity) && !ValidSeverities.Contains(severity))
        {
            invocation.MarkFailure("validation: unrecognized severity");
            return ToolExecutionHelper.ValidationError<AnalyzeSolutionResponse>(
                $"Unrecognized severity '{severity}'.",
                invocation.CorrelationId,
                "Valid severity values are: Error, Warning, Info, Hidden (case-insensitive).");
        }

        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await analyzerService.AnalyzeSolutionAsync(
                pathOrGit,
                branch,
                include,
                exclude,
                severity,
                maxDiagnostics,
                progress,
                timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<AnalyzeSolutionResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<AnalyzeSolutionResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<AnalyzeSolutionResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
