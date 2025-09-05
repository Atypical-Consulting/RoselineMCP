using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MyFirstMCP.Services;

namespace MyFirstMCP.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool]
    [Description("Analyze a C# solution and return diagnostics summary with details about errors, warnings, and info messages")]
    public static async Task<string> AnalyzeSolution(
        SolutionAnalyzerService analyzerService,
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
    
    [McpServerTool]
    [Description("List detailed diagnostics for a specific project with statistics and fixable suggestions")]
    public static async Task<string> ListDiagnostics(
        SolutionAnalyzerService analyzerService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Optional list of diagnostic IDs to filter (e.g., ['CS0168', 'CS0219'])")]
        string[]? ids = null,
        [Description("Optional list of file patterns to filter (e.g., ['Controller.cs', 'Service.cs'])")]
        string[]? files = null,
        [Description("Maximum number of diagnostic details to return (default: 100)")]
        int max = 100)
    {
        try
        {
            var result = await analyzerService.ListDiagnosticsAsync(
                project,
                ids?.ToList(),
                files?.ToList(),
                max);
            
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