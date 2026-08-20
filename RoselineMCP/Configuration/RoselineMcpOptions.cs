namespace RoselineMCP.Configuration;

/// <summary>
/// Strongly-typed options bound from the "RoselineMCP" configuration section
/// (appsettings.json / appsettings.{Environment}.json / ROSELINE_ environment variables).
/// </summary>
public class RoselineMcpOptions
{
    /// <summary>
    /// The shipped default for <see cref="ConfirmDestructiveWritesTimeout"/>, in milliseconds.
    /// Exposed so callers that have no <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>
    /// at all (outside DI, as in unit tests) can fall back to the documented default rather than
    /// duplicating the constant and letting the two drift.
    /// </summary>
    public const int DefaultConfirmDestructiveWritesTimeoutMs = 300_000;

    /// <summary>
    /// Wall-clock <em>analysis</em> budget, in milliseconds, applied to each MCP tool invocation in
    /// addition to the caller's own request cancellation token. A value of zero or less disables
    /// the wall-clock timeout (only the caller's cancellation still applies).
    /// <para>
    /// It does not cover the whole invocation on the three write tools. When a write confirmation
    /// is elicited, this clock is armed only <em>after</em> the human round-trip resolves, so
    /// think-time is not charged against an analysis budget it may legitimately exceed. That wait
    /// is bounded separately by <see cref="ConfirmDestructiveWritesTimeout"/>. Size this value for
    /// the analysis, not for the wall-clock span a caller observes.
    /// </para>
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

    /// <summary>
    /// Whether the diagnostics tools (<c>AnalyzeSolution</c>, <c>ListDiagnostics</c>,
    /// <c>ApplyFixes</c>) run Roslyn analyzers on top of compiler diagnostics. Enabled by
    /// default: the bundled Roslynator analyzers plus any analyzers the target project itself
    /// references are executed via <c>CompilationWithAnalyzers</c>, so RCS*/custom-analyzer
    /// diagnostics surface and are fixable. Set to <see langword="false"/> for compiler-only
    /// diagnostics — faster, but no analyzer diagnostics and no analyzer-backed fixes. Note that
    /// running a target project's own analyzer references executes third-party code at analysis
    /// time; see SECURITY.md.
    /// </summary>
    public bool RunAnalyzers { get; set; } = true;

    /// <summary>
    /// Whether the write tools (<c>ApplyFixes</c>, <c>EditMember</c>, <c>RenameSymbol</c>) ask the
    /// connected client to confirm — via MCP elicitation — before writing when the caller passed
    /// <c>previewOnly: false</c>. Enabled by default. Set to <see langword="false"/> for unattended
    /// hosts (CI, headless agents) whose client cannot answer an elicitation: the explicit
    /// <c>previewOnly: false</c> opt-in then stands as the only guard before a write. See SECURITY.md.
    /// </summary>
    public bool ConfirmDestructiveWrites { get; set; } = true;

    /// <summary>
    /// How long, in milliseconds, to wait for the client's answer to the write-confirmation
    /// elicitation before treating the silence as "no". Defaults to 5 minutes. A value of zero or
    /// less removes the bound (wait indefinitely, as before this option existed). This is
    /// deliberately NOT
    /// <see cref="DefaultTimeout"/> — that is an analysis budget, and human think-time must not be
    /// charged against it. On expiry the write is downgraded to a preview rather than proceeding:
    /// "I asked and you said nothing" is not consent. Unattended hosts that want writes without a
    /// human should set <see cref="ConfirmDestructiveWrites"/> to <see langword="false"/> instead.
    /// </summary>
    public int ConfirmDestructiveWritesTimeout { get; set; } = DefaultConfirmDestructiveWritesTimeoutMs;

    /// <summary>
    /// The shipped default for <see cref="GuardTimeout"/>, in milliseconds. Exposed for the same
    /// reason as <see cref="DefaultConfirmDestructiveWritesTimeoutMs"/>: callers outside DI should
    /// fall back to the documented default rather than duplicating the constant.
    /// </summary>
    public const int DefaultGuardTimeoutMs = 10_000;

    /// <summary>
    /// Whether the server opens the local compile-guard endpoint, which lets the
    /// <c>roseline-mcp guard</c> hook client ask "did the write that just happened break the build?"
    /// and reuse this process's already-warm <c>MSBuildWorkspace</c>.
    /// <para>
    /// <see langword="false"/> by default. This is an opt-in for two independent reasons: it adds a
    /// local IPC surface (see SECURITY.md), and it makes the server answer questions nobody asked it
    /// through the MCP channel. An operator turns it on deliberately, the same way
    /// <c>previewOnly: false</c> is passed deliberately.
    /// </para>
    /// </summary>
    public bool Guard { get; set; }

    /// <summary>
    /// Explicit path for the guard endpoint — a named pipe on Windows, a Unix domain socket
    /// elsewhere. <see langword="null"/> (the default) derives a per-user path, which is what keeps
    /// two users on one machine from sharing an endpoint. Ignored when <see cref="Guard"/> is
    /// <see langword="false"/>.
    /// </summary>
    public string? GuardEndpoint { get; set; }

    /// <summary>
    /// How long, in milliseconds, the guard hook client waits for the server's verdict before giving
    /// up <b>silently</b>. Defaults to 10 seconds.
    /// <para>
    /// Deliberately NOT <see cref="DefaultTimeout"/>: that is an analysis budget sized for a tool
    /// call a caller is waiting on, while this bounds a hook the agent harness will itself kill —
    /// the same separation-of-clocks argument as <see cref="ConfirmDestructiveWritesTimeout"/>.
    /// Expiry is never an error: a guard that cannot inform must not be able to interrupt.
    /// </para>
    /// </summary>
    public int GuardTimeout { get; set; } = DefaultGuardTimeoutMs;
}
