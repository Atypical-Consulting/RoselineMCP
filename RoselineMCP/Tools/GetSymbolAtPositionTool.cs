using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool resolving a <c>file:line(:column)</c> position to the symbol living there — the bridge
/// from a diagnostic, stack trace, or grep hit to the symbol-name-based navigation tools, without
/// reading the file to guess a name.
/// </summary>
[McpServerToolType]
public static class GetSymbolAtPositionTool
{
    /// <summary>
    /// Returns the symbol at a 1-based file:line(:column) position, with its name, kind, signature,
    /// definition location, and whether the position is the symbol's own declaration.
    /// </summary>
    [McpServerTool(Title = "Get Symbol At Position", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("You have a file and line (from a diagnostic, stack trace, grep, or find_references) and want to know what C# symbol lives there — prefer this over Read. Resolves the position to the symbol (declared or referenced), returning its name, kind, signature, definition location, and isDeclaration; feed the fullName straight into get_symbol_info/find_references. Read-only: never modifies any files on disk. Limitations: file matches by name/path suffix, so an ambiguous suffix can resolve the wrong file; line-only prefers declarations."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit)]
    public static async Task<ToolResult<SymbolAtPositionResponse>> GetSymbolAtPosition(
        ICodeNavigationService navigationService,
        [Description("File to resolve the position in (name or path suffix, e.g. 'UserService.cs' or 'Services/UserService.cs')")]
        string file,
        [Description("1-based line number of the position")]
        int line,
        [Description("Optional 1-based column. Omit to resolve the most relevant symbol on the line (declarations win over references).")]
        int? column = null,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(GetSymbolAtPosition), loggerFactory, server);

        if (string.IsNullOrWhiteSpace(file))
        {
            invocation.MarkFailure("validation: no file");
            return ToolExecutionHelper.ValidationError<SymbolAtPositionResponse>(
                "Provide a 'file' (name or path suffix) to resolve a position in.",
                invocation.CorrelationId,
                "Pass the file the position refers to, e.g. file: \"UserService.cs\", line: 42.");
        }

        if (line < 1 || column is < 1)
        {
            invocation.MarkFailure("validation: non-positive line/column");
            return ToolExecutionHelper.ValidationError<SymbolAtPositionResponse>(
                $"Invalid position {line}:{(column?.ToString() ?? "?")} — line and column numbers are 1-based.",
                invocation.CorrelationId,
                "Pass line >= 1 (and column >= 1 when supplied), as reported by diagnostics and stack traces.");
        }

        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            var result = await navigationService.GetSymbolAtPositionAsync(
                project, file, line, column, timeoutSource.Token);

            invocation.MarkSuccess();
            return ToolResult<SymbolAtPositionResponse>.Success(result);
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<SymbolAtPositionResponse>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<SymbolAtPositionResponse>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
