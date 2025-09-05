using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoselineMCP.Services;
using RoselineMCP.Interfaces;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool for creating unified diff patches between two text blobs.
/// </summary>
[McpServerToolType]
public static class CreatePatchTool
{
    /// <summary>
    /// Creates a unified diff patch between two text blobs.
    /// </summary>
    [McpServerTool]
    [Description("Create a unified diff patch between two text blobs")]
    public static string CreatePatch(
        IPatchService patchService,
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