using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;

namespace RoselineMCP.Tools;

/// <summary>
/// MCP tool answering "does this compile, and what broke" against on-disk state — the replacement
/// for a <c>dotnet build</c> round trip in an agent's edit loop.
/// </summary>
/// <remarks>
/// It answers about whatever is on disk, whoever wrote it, so it also serves agents that never call
/// RoselineMCP's write tools. The saving comes from the warm <c>MSBuildWorkspace</c> the server
/// already holds: the first call of a session pays the cold load, every call after it reuses an
/// incremental Roslyn compilation.
/// </remarks>
[McpServerToolType]
public static class CheckCompilationTool
{
    /// <summary>
    /// Compiles the loaded solution and reports whether it compiles, with the compiler errors.
    /// </summary>
    [McpServerTool(Title = "Check Compilation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description(
        "Does this code compile right now, and what broke? Compiler errors only, in under a second on a warm workspace — "
        + "use this INSTEAD OF running `dotnet build` after an edit, and after edits made by any tool, not just RoselineMCP's. "
        + "Read-only: never modifies any files on disk. "
        + "For an exploratory inventory of code quality — analyzer diagnostics (Roslynator RCS*, StyleCop, the project's own "
        + "analyzers), severity statistics and which IDs are auto-fixable — use list_diagnostics instead; it is the slower, "
        + "broader tool. Rule of thumb: check_compilation answers \"is it still building?\", list_diagnostics answers \"what should I clean up?\". Limitations: compiler diagnostics only, no analyzers; scopeComplete=false means not every dependent was seen."
        + RoselineToolDescriptions.ProjectAutoDiscoveryLimit
        + " Example: check_compilation{} -> resolvedPath + compiles + errors[] + scope/scopeComplete.")]
    public static async Task<ToolResult<VerificationVerdict>> CheckCompilation(
        IProjectLoader projectLoader,
        IVerificationService verificationService,
        [Description("Project name, directory, .csproj, or .sln path. Optional — if omitted, RoselineMCP auto-discovers the solution/project from its working directory.")]
        string? project = null,
        [Description("Maximum number of errors to return (default: 20); the remainder are counted in `omitted`.")]
        int max = 20,
        IOptions<RoselineMcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = ToolExecutionHelper.BeginInvocation(nameof(CheckCompilation), loggerFactory, server);
        using var timeoutSource = ToolExecutionHelper.CreateLinkedTimeoutSource(cancellationToken, options);

        try
        {
            using var loaded = await projectLoader.LoadAsync(project, timeoutSource.Token);
            try
            {

                // No baseline: an absolute verdict about what is on disk, which is the question asked.
                var verdict = await verificationService.VerifyAsync(
                    baseline: null, loaded.Solution, max, timeoutSource.Token);
                verdict.ResolvedPath = loaded.ResolvedPath;

                invocation.MarkSuccess();
                return ToolResult<VerificationVerdict>.Success(verdict);
            }
            catch (Exception ex)
            {
                // Name the checkout that answered, so the failure envelope can tell
                // "the symbol is not here" apart from "you asked the wrong checkout".
                ResolvedPathStamp.Stamp(ex, loaded);
                throw;
            }
        }
        catch (OperationCanceledException cancellation)
        {
            invocation.MarkFailure("cancelled");
            return ToolExecutionHelper.Cancellation<VerificationVerdict>(cancellationToken, timeoutSource, options, invocation.CorrelationId, cancellation);
        }
        catch (Exception ex)
        {
            invocation.MarkFailure(ex.Message);
            return ToolExecutionHelper.Error<VerificationVerdict>(ex, invocation.CorrelationId, invocation.Logger);
        }
    }
}
