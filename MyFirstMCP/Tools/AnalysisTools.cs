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
    
    [McpServerTool]
    [Description("Apply code fixes for specified diagnostic IDs in a project")]
    public static async Task<string> ApplyFixes(
        CodeFixService codeFixService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("List of diagnostic IDs to fix (e.g., ['RCS1213', 'SA1101'])")]
        string[] ids,
        [Description("If true, only preview changes without applying them (default: false)")]
        bool previewOnly = false)
    {
        try
        {
            if (ids == null || ids.Length == 0)
            {
                var errorResponse = new
                {
                    error = "No diagnostic IDs provided",
                    type = "ValidationError"
                };
                return JsonSerializer.Serialize(errorResponse);
            }
            
            var result = await codeFixService.ApplyFixesAsync(
                project,
                ids.ToList(),
                previewOnly);
            
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
    [Description("Create a unified diff patch between two text blobs")]
    public static string CreatePatch(
        PatchService patchService,
        [Description("The original text content (before changes)")]
        string before,
        [Description("The modified text content (after changes)")]
        string after,
        [Description("Optional file name for the patch header (default: 'file.txt')")]
        string? fileName = null)
    {
        try
        {
            var result = patchService.CreatePatch(before, after, fileName);
            
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