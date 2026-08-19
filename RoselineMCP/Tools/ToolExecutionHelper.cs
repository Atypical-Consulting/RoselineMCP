using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Diagnostics;
using RoselineMCP.Models;

namespace RoselineMCP.Tools;

/// <summary>
/// The closed, stable set of machine-readable error "type" values returned by every MCP tool.
/// Raw CLR exception type names (<c>ex.GetType().Name</c>) are never surfaced directly — every
/// failure is classified into one of these categories so callers can branch on a documented
/// contract instead of an implementation detail that can change with any dependency upgrade.
/// </summary>
internal static class ToolErrorTypes
{
    /// <summary>Caller-supplied input was missing, malformed, or otherwise invalid (e.g. an unrecognized severity string, no diagnostic IDs provided).</summary>
    public const string Validation = "ValidationError";

    /// <summary>The requested solution, project, or file could not be located.</summary>
    public const string NotFound = "NotFoundError";

    /// <summary>The operation failed while analyzing, building, or fetching the target (e.g. MSBuild workspace load failure, Git clone failure).</summary>
    public const string Analysis = "AnalysisError";

    /// <summary>The caller's request was cancelled before completion.</summary>
    public const string Cancelled = "CancelledError";

    /// <summary>The operation exceeded the configured wall-clock timeout (RoselineMCP:DefaultTimeout).</summary>
    public const string Timeout = "TimeoutError";

    /// <summary>An unexpected, unclassified failure occurred. Full detail is logged server-side; the response never leaks raw exception text or stack traces.</summary>
    public const string Internal = "InternalError";
}

/// <summary>
/// Per-invocation tracing/correlation context created at the very start of each MCP tool method
/// (via <see cref="ToolExecutionHelper.BeginInvocation"/>) and disposed when the method returns.
/// Bundles three things every tool needs for lightweight, opt-in observability:
/// <list type="bullet">
/// <item>A per-call <see cref="CorrelationId"/> (a GUID), always generated — it is cheap, and is
/// threaded into every JSON error response so a user reporting a failure can supply one ID that
/// ties back to full server-side logs.</item>
/// <item>An <see cref="Activity"/> span from <see cref="RoselineDiagnostics.ActivitySource"/>
/// tagged with the tool name and correlation ID. This is a no-op unless diagnostic tracing is
/// enabled (RoselineMCP:EnableDiagnosticLogging) — see <see cref="RoselineDiagnostics"/>.</item>
/// <item>An <see cref="ILogger"/> scope (<see cref="ILogger.BeginScope{TState}"/>) so every log
/// line emitted while the tool runs carries the same correlation ID (visible on stderr since
/// Logging:Console:IncludeScopes is enabled).</item>
/// </list>
/// </summary>
internal sealed class ToolInvocation : IDisposable
{
    private readonly Activity? _activity;
    private readonly IDisposable? _logScope;
    private readonly ILogger? _clientLogger;
    private readonly string _toolName;

    /// <summary>Per-invocation correlation ID, generated once at the start of the tool call.</summary>
    public string CorrelationId { get; } = Guid.NewGuid().ToString("n");

    /// <summary>Logger for this tool invocation, scoped with the correlation ID; <see langword="null"/> if no factory was supplied.</summary>
    public ILogger? Logger { get; }

    public ToolInvocation(string toolName, ILoggerFactory? loggerFactory, McpServer? server = null)
    {
        _toolName = toolName;
        _activity = RoselineDiagnostics.ActivitySource.StartActivity(toolName);
        _activity?.SetTag(ActivityTags.ToolName, toolName);
        _activity?.SetTag(ActivityTags.CorrelationId, CorrelationId);

        Logger = loggerFactory?.CreateLogger($"RoselineMCP.Tools.{toolName}");
        _logScope = Logger?.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = CorrelationId,
            ["Tool"] = toolName
        });

        // A logger that forwards to the connected client as MCP `notifications/message` (only when
        // the client has opted into logging at a matching level — otherwise a no-op). This surfaces
        // the correlation ID in the client's own log stream, not just in the tool result.
        //
        // SEP-2577 deprecated the Logging feature in protocol revision 2026-07-28, so this pipeline is
        // live only for clients that negotiate 2025-11-25 or earlier; on a newer session it stays a
        // no-op (the SDK gates it on a level the client can no longer set). The correlation ID still
        // reaches every caller through the error envelope and the server's own stderr log, which is
        // what SEP-2577 points to as the replacement, so nothing is lost by leaving this in place for
        // the clients that do still use it. Deprecated APIs stay in the spec for at least twelve
        // months, so this keeps working meanwhile; revisit when the SDK offers a supported successor.
#pragma warning disable MCP9005 // AsClientLoggerProvider is deprecated, but remains the gated client-log pipeline.
        try
        {
            _clientLogger = server?.AsClientLoggerProvider().CreateLogger($"RoselineMCP.Tools.{toolName}");
        }
        catch
        {
            _clientLogger = null;
        }
#pragma warning restore MCP9005
    }

    /// <summary>Marks the current span as having completed successfully.</summary>
    public void MarkSuccess() => _activity?.SetStatus(ActivityStatusCode.Ok);

    /// <summary>
    /// Marks the current span as failed, recording <paramref name="reason"/> as the status
    /// description, and emits a client-facing log notification carrying the correlation ID so the
    /// caller can tie the failure back to the full server-side log entry.
    /// </summary>
    public void MarkFailure(string reason)
    {
        _activity?.SetStatus(ActivityStatusCode.Error, reason);
        try
        {
            _clientLogger?.LogWarning(
                "Tool {Tool} failed (correlationId={CorrelationId}): {Reason}", _toolName, CorrelationId, reason);
        }
        catch
        {
            // Client-facing logging is best-effort; never let it affect the tool result.
        }
    }

    public void Dispose()
    {
        _logScope?.Dispose();
        _activity?.Dispose();
    }
}

/// <summary>
/// Shared helper used by every MCP tool method to combine the caller's request cancellation
/// token with the configurable wall-clock timeout (RoselineMCP:DefaultTimeout), to start the
/// per-invocation tracing/correlation context, and to build a consistent typed failure envelope
/// (<see cref="ToolResult{T}"/>) when an operation is cancelled, times out, or fails. Tools never
/// throw to the MCP protocol layer — see the "Error Resilience" convention in CLAUDE.md.
/// </summary>
internal static class ToolExecutionHelper
{
    /// <summary>
    /// Starts the per-invocation tracing/correlation context for a tool call. Call this first,
    /// before any validation, so every code path — including early validation failures — gets a
    /// correlation ID and is covered by the Activity span. When <paramref name="server"/> is
    /// supplied, tool failures are also surfaced to the client as MCP log notifications. See
    /// <see cref="ToolInvocation"/>.
    /// </summary>
    public static ToolInvocation BeginInvocation(
        string toolName, ILoggerFactory? loggerFactory, McpServer? server = null) =>
        new(toolName, loggerFactory, server);

    /// <summary>
    /// Best-effort confirmation gate for a destructive, disk-writing operation. Returns
    /// <see langword="true"/> if the write should proceed, <see langword="false"/> only if the
    /// caller's client actively declined it.
    /// </summary>
    /// <remarks>
    /// This is a second guard behind the <c>previewOnly: false</c> opt-in, surfaced to the human via
    /// MCP elicitation. It is deliberately best-effort: if no server is available, or the connected
    /// client does not support elicitation (or the elicitation round-trip fails for any reason other
    /// than cancellation), the explicit <c>previewOnly: false</c> opt-in stands and the write
    /// proceeds — a client without elicitation support must not be silently prevented from writing.
    /// Only an explicit decline (<see cref="ElicitResult.IsAccepted"/> is <see langword="false"/>)
    /// stops the write.
    /// </remarks>
    public static async Task<bool> ConfirmDestructiveWriteAsync(
        McpServer? server,
        string message,
        CancellationToken cancellationToken)
    {
        if (server is null)
        {
            return true;
        }

        try
        {
            // A field-less form: the user simply accepts or declines the confirmation prompt.
            var request = new ElicitRequestParams
            {
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema(),
            };
            var result = await server.ElicitAsync(request, cancellationToken);
            return result.IsAccepted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Client does not support elicitation (or it failed) — honor the explicit opt-in.
            return true;
        }
    }

    /// <summary>
    /// Creates a <see cref="CancellationTokenSource"/> linked to <paramref name="requestToken"/>
    /// that is also cancelled once the configured DefaultTimeout elapses. A DefaultTimeout of
    /// zero or less (or missing configuration) disables the wall-clock timeout, leaving only the
    /// caller's own cancellation in effect.
    /// </summary>
    public static CancellationTokenSource CreateLinkedTimeoutSource(
        CancellationToken requestToken,
        IOptions<RoselineMcpOptions>? options)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);

        var timeoutMs = options?.Value.DefaultTimeout ?? 0;
        if (timeoutMs > 0)
        {
            cts.CancelAfter(timeoutMs);
        }

        return cts;
    }

    /// <summary>
    /// Builds the typed failure envelope for a cancelled operation, distinguishing a
    /// caller-initiated cancellation from a wall-clock timeout. <paramref name="correlationId"/>
    /// (see <see cref="ToolInvocation.CorrelationId"/>) is echoed back so a user reporting the
    /// failure can supply one ID that ties back to full server-side logs.
    /// </summary>
    public static ToolResult<T> Cancellation<T>(
        CancellationToken requestToken,
        CancellationTokenSource timeoutSource,
        IOptions<RoselineMcpOptions>? options,
        string correlationId)
    {
        if (!requestToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            var timeoutMs = options?.Value.DefaultTimeout ?? 0;
            return ToolResult<T>.Failure(new ToolError
            {
                Type = ToolErrorTypes.Timeout,
                Message = $"Operation timed out after {timeoutMs}ms",
                CorrelationId = correlationId
            });
        }

        return ToolResult<T>.Failure(new ToolError
        {
            Type = ToolErrorTypes.Cancelled,
            Message = "Operation was cancelled",
            CorrelationId = correlationId
        });
    }

    /// <summary>
    /// Builds the typed failure envelope for a caller-input validation failure detected before
    /// invoking the underlying service (e.g. a missing required argument or an unrecognized
    /// enum-like string). Unlike <see cref="Error{T}"/>, the caller supplies the message directly
    /// since no exception has been thrown yet. <paramref name="hint"/> should suggest the
    /// concrete corrective action, e.g. the set of accepted values or which tool to call first.
    /// <paramref name="correlationId"/> (see <see cref="ToolInvocation.CorrelationId"/>) is echoed
    /// back so a user reporting the failure can supply one ID that ties back to full server-side logs.
    /// </summary>
    public static ToolResult<T> ValidationError<T>(string message, string correlationId, string? hint = null) =>
        ToolResult<T>.Failure(new ToolError
        {
            Type = ToolErrorTypes.Validation,
            Message = message,
            Hint = hint,
            CorrelationId = correlationId
        });

    /// <summary>
    /// Builds the standard typed failure envelope used across every tool for unhandled exceptions,
    /// classifying <paramref name="ex"/> into the closed <see cref="ToolErrorTypes"/> set instead
    /// of surfacing its raw CLR type name. <see cref="ToolErrorTypes.Internal"/>-class failures
    /// are logged in full via <paramref name="logger"/> (never returned to the caller) and are
    /// reported with a short, stable, user-safe message to avoid leaking internal exception text
    /// or stack traces. <paramref name="correlationId"/> (see <see cref="ToolInvocation.CorrelationId"/>)
    /// is always echoed back — including for InternalError responses — so a user reporting the
    /// failure can supply one ID that ties back to the full, unredacted server-side log entry.
    /// </summary>
    public static ToolResult<T> Error<T>(
        Exception ex,
        string correlationId,
        ILogger? logger = null,
        [CallerMemberName] string? toolName = null)
    {
        var type = Classify(ex);
        string message;

        if (type == ToolErrorTypes.Internal)
        {
            logger?.LogError(ex, "Unhandled internal error in {Tool}", toolName);
            message = "An unexpected internal error occurred. Check the server logs for details.";
        }
        else
        {
            message = ex.Message;
        }

        return ToolResult<T>.Failure(new ToolError
        {
            Type = type,
            Message = message,
            CorrelationId = correlationId
        });
    }

    /// <summary>
    /// Maps an exception onto the closed <see cref="ToolErrorTypes"/> set. Anything not
    /// recognized here is treated as <see cref="ToolErrorTypes.Internal"/> so unclassified
    /// failures fail safe (no raw detail leaked) rather than silently exposing implementation
    /// details through an unclassified/raw type name.
    /// </summary>
    private static string Classify(Exception ex) => ex switch
    {
        ArgumentException or FormatException => ToolErrorTypes.Validation,
        FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => ToolErrorTypes.NotFound,
        InvalidOperationException or TimeoutException or IOException => ToolErrorTypes.Analysis,
        _ => ToolErrorTypes.Internal
    };
}
