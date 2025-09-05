using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoselineMCP.Interfaces;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for applying code fixes for specified diagnostic IDs in a project.
/// </summary>
[McpServerToolType]
public static class ApplyFixesTool
{
    /// <summary>
    /// Applies code fixes for specified diagnostic IDs in a project.
    /// </summary>
    [McpServerTool]
    [Description("Apply code fixes for specified diagnostic IDs in a project")]
    public static async Task<string> ApplyFixes(
        ICodeFixService codeFixService,
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
}