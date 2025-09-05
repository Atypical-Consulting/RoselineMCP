using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoselineMCP.Services;

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
    [McpServerTool]
    [Description("List detailed diagnostics for a specific project with statistics and fixable suggestions")]
    public static async Task<string> ListDiagnostics(
        ISolutionAnalyzerService analyzerService,
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