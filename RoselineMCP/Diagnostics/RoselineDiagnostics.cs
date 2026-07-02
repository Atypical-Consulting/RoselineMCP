using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RoselineMCP.Diagnostics;

/// <summary>
/// Central, dependency-free tracing primitives for RoselineMCP's four MCP tools.
/// </summary>
/// <remarks>
/// This deliberately uses the built-in <see cref="System.Diagnostics.ActivitySource"/> /
/// <see cref="Activity"/> APIs instead of the OpenTelemetry SDK: they ship with the .NET runtime,
/// are already OTel-compatible for anyone who wants to bridge them to a real collector via a
/// standard <see cref="ActivityListener"/>, and keep this reference implementation free of extra
/// package weight.
///
/// Tracing is strictly opt-in and stdio-safe:
/// <list type="bullet">
/// <item>Unless <see cref="RegisterStderrListener"/> is called (gated by the
/// "RoselineMCP:EnableDiagnosticLogging" configuration key — see
/// <see cref="RoselineMCP.Configuration.RoselineMcpOptions.EnableDiagnosticLogging"/>), no
/// <see cref="ActivityListener"/> is registered against <see cref="ActivitySource"/>. With no
/// listener, <c>ActivitySource.StartActivity(...)</c> always returns <see langword="null"/>, so
/// every tag/status call each tool makes on it is a cheap no-op.</item>
/// <item>When enabled, span start/stop is written exclusively through <see cref="ILogger"/>
/// (already routed to stderr by Program.cs) — never to stdout, which carries the MCP JSON-RPC
/// channel, and never over the network. No OTLP or other exporter is configured.</item>
/// </list>
/// </remarks>
public static class RoselineDiagnostics
{
    /// <summary>Name of the shared <see cref="ActivitySource"/> every MCP tool starts spans from.</summary>
    public const string SourceName = "RoselineMCP";

    /// <summary>Shared source used by every MCP tool to start a per-invocation <see cref="Activity"/>.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Registers an <see cref="ActivityListener"/> that samples every <see cref="Activity"/>
    /// started from <see cref="ActivitySource"/> and, when each one stops, logs its tool name,
    /// correlation ID, outcome, and duration through <paramref name="logger"/>. Call once at
    /// startup, only when diagnostic tracing is enabled. Dispose the returned listener on shutdown
    /// to stop listening (also cleaned up automatically on process exit).
    /// </summary>
    public static ActivityListener RegisterStderrListener(ILogger logger)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var outcome = activity.Status == ActivityStatusCode.Error ? "failure" : "success";

                // Logged at Information (not Debug): this listener is only ever registered when
                // RoselineMCP:EnableDiagnosticLogging is explicitly turned on, so the opt-in already
                // gates the noise — the trace line itself must still clear Program.cs's default
                // "RoselineMCP" category minimum level (Information in the default/Production
                // environment) or the documented `ROSELINE_RoselineMCP__EnableDiagnosticLogging=true`
                // workflow would silently produce no output outside Development.
                logger.LogInformation(
                    "[trace] tool={Tool} correlationId={CorrelationId} outcome={Outcome} durationMs={DurationMs:F1}",
                    activity.DisplayName,
                    activity.GetTagItem(ActivityTags.CorrelationId),
                    outcome,
                    activity.Duration.TotalMilliseconds);
            }
        };

        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>Well-known tag names used on the <see cref="Activity"/> spans started for each MCP tool call.</summary>
internal static class ActivityTags
{
    public const string ToolName = "mcp.tool";
    public const string CorrelationId = "mcp.correlation_id";
}
