namespace RoselineMCP.Configuration;

/// <summary>
/// Strongly-typed options bound from the "RoselineMCP" configuration section
/// (appsettings.json / appsettings.{Environment}.json / ROSELINE_ environment variables).
/// </summary>
public class RoselineMcpOptions
{
    /// <summary>
    /// Wall-clock timeout, in milliseconds, applied to each MCP tool invocation in addition to
    /// the caller's own request cancellation token. A value of zero or less disables the
    /// wall-clock timeout (only the caller's cancellation still applies).
    /// </summary>
    public int DefaultTimeout { get; set; } = 120_000;

    /// <summary>
    /// Opt-in switch for lightweight, local tracing of MCP tool invocations. When
    /// <see langword="false"/> (the default), no <see cref="System.Diagnostics.ActivityListener"/>
    /// is registered, so the per-tool <see cref="System.Diagnostics.Activity"/> spans created by
    /// RoselineMCP.Diagnostics.RoselineDiagnostics are never sampled and cost next to nothing. When
    /// <see langword="true"/>, span start/stop (tool name, correlation ID, outcome, duration) is
    /// logged through the existing <c>ILogger</c> pipeline, which is already routed to stderr —
    /// this never touches stdout (the MCP JSON-RPC channel) and never leaves the machine (no
    /// network exporter is configured).
    /// </summary>
    public bool EnableDiagnosticLogging { get; set; }

    /// <summary>
    /// Whether the navigation/edit tools reuse the loaded <c>MSBuildWorkspace</c> across tool
    /// calls (see <c>RoselineMCP.Services.CachingProjectLoader</c>). Enabled by default: cached
    /// solutions are fingerprinted (last-write-time + length of the <c>.sln</c>, every
    /// <c>.csproj</c>, and every document) and transparently reloaded whenever anything changed on
    /// disk. Set to <see langword="false"/> to restore the previous behavior of loading a fresh
    /// workspace on every call.
    /// </summary>
    public bool WorkspaceCache { get; set; } = true;
}
