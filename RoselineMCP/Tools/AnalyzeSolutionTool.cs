using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoselineMCP.Services;

namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for analyzing C# solutions and returning diagnostics summary.
/// </summary>
[McpServerToolType]
public static class AnalyzeSolutionTool
{
    /// <summary>
    /// Analyzes a C# solution and returns diagnostics summary with details about errors, warnings, and info messages.
    /// </summary>
    [McpServerTool]
    [Description("Analyze a C# solution and return diagnostics summary with details about errors, warnings, and info messages")]
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
        int maxDiagnostics = 100)
    {
        try
        {
            var result = await analyzerService.AnalyzeSolutionAsync(
                pathOrGit,
                branch,
                include,
                exclude,
                severity,
                maxDiagnostics);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            return json;
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                error = ex.Message,
                type = ex.GetType().Name
            };

            return JsonSerializer.Serialize(errorResponse);
        }
    }
}