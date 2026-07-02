using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
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
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Analyze a C# solution and return diagnostics summary with details about errors, warnings, and info messages. Read-only: never modifies any files on disk.")]
    public static async Task<string> AnalyzeSolution(
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
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(AnalyzeSolution), loggerFactory);

        if (!string.IsNullOrEmpty(severity) && !ValidSeverities.Contains(severity))
        {
            invocation.MarkFailure("validation: unrecognized severity");
            return ToolExecutionHelper.SerializeValidationError(
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
                timeoutSource.Token);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            invocation.MarkSuccess();
            return json;
        }
        catch (OperationCanceledException)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.SerializeCancellation(cancellationToken, timeoutSource, options, invocation.CorrelationId);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.SerializeError(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}