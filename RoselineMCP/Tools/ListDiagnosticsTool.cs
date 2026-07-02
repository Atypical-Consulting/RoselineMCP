using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
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
    [McpServerTool(ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List detailed diagnostics for a specific project with statistics and fixable suggestions. Read-only: never modifies any files on disk.")]
    public static async Task<string> ListDiagnostics(
        ISolutionAnalyzerService analyzerService,
        [Description("Project name or path to .csproj file")]
        string project,
        [Description("Optional list of diagnostic IDs to filter (e.g., ['CS0168', 'CS0219'])")]
        string[]? ids = null,
        [Description("Optional list of file patterns to filter (e.g., ['Controller.cs', 'Service.cs'])")]
        string[]? files = null,
        [Description("Maximum number of diagnostic details to return (default: 100)")]
        int max = 100,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(ListDiagnostics), loggerFactory);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await analyzerService.ListDiagnosticsAsync(
                project,
                ids?.ToList(),
                files?.ToList(),
                max,
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