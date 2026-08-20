using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoselineMCP.Configuration;
using RoselineMCP.Diagnostics;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;

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
/// The outcome of the write-confirmation gate (see
/// <see cref="ToolExecutionHelper.ConfirmDestructiveWriteAsync"/>). Only <see cref="Proceed"/>
/// permits a write; the other two both downgrade the operation to a preview but are kept apart so
/// the caller can tell the user which happened — "you said no" and "nobody answered" are different
/// facts, and only the second suggests the client may need
/// <c>RoselineMCP:ConfirmDestructiveWrites=false</c>.
/// </summary>
internal enum WriteConfirmation
{
    /// <summary>
    /// The write may go ahead: the client accepted it, could not be asked at all, or the operator
    /// switched the gate off for this deployment.
    /// </summary>
    Proceed,

    /// <summary>The client was asked and actively declined.</summary>
    Declined,

    /// <summary>
    /// The client was asked and did not answer within
    /// <see cref="RoselineMcpOptions.ConfirmDestructiveWritesTimeout"/>.
    /// </summary>
    TimedOut,
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
    /// <see cref="WriteConfirmation.Proceed"/> if the write should proceed, and
    /// <see cref="WriteConfirmation.Declined"/> or <see cref="WriteConfirmation.TimedOut"/> only if
    /// the caller's client actively declined it or never answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a second guard behind the <c>previewOnly: false</c> opt-in, surfaced to the human via
    /// MCP elicitation. It is deliberately best-effort: if no server is available, or the connected
    /// client does not support elicitation (or the elicitation round-trip fails for any reason other
    /// than cancellation), the explicit <c>previewOnly: false</c> opt-in stands and the write
    /// proceeds — a client without elicitation support must not be silently prevented from writing.
    /// Only an explicit decline (<see cref="ElicitResult.IsAccepted"/> is <see langword="false"/>)
    /// or an unanswered prompt stops the write.
    /// </para>
    /// <para>
    /// The round-trip is bounded by <see cref="RoselineMcpOptions.ConfirmDestructiveWritesTimeout"/>
    /// on a clock of its own — deliberately not <see cref="RoselineMcpOptions.DefaultTimeout"/>,
    /// which is an analysis budget a human reading a real diff may legitimately exceed. On expiry
    /// the result is <see cref="WriteConfirmation.TimedOut"/> and the caller downgrades to a
    /// preview: a client that <em>cannot</em> answer justifies assuming consent, but one that was
    /// asked and said nothing does not. A real request cancellation is distinguished from that
    /// deadline by the caller's own token and still propagates.
    /// </para>
    /// <para>
    /// An operator can switch the gate off for a whole deployment by setting
    /// <c>RoselineMCP:ConfirmDestructiveWrites</c> to <see langword="false"/> (see
    /// <see cref="RoselineMcpOptions.ConfirmDestructiveWrites"/>) — intended for unattended hosts
    /// (CI, headless agents) whose client can elicit but has no human to answer, which is the one
    /// case the best-effort fallbacks above do not already cover. No elicitation is sent at all
    /// then, so the <c>previewOnly: false</c> opt-in is the only remaining guard before a write.
    /// </para>
    /// <para>
    /// The prompt is composed here rather than handed in ready-made, because naming the write target
    /// means resolving it and resolution is no longer free (see <see cref="ResolveWriteTarget"/>).
    /// Every path that will not send a prompt therefore returns before resolving: a deployment with
    /// the gate switched off, and a client that cannot be asked, must neither pay for nor fail on a
    /// question they will never see. The resolved target is returned alongside the answer so the
    /// caller can write to the exact path the human approved.
    /// </para>
    /// </remarks>
    private static async Task<(WriteConfirmation Confirmation, string? ResolvedTarget)> ConfirmDestructiveWriteAsync(
        McpServer? server,
        IOptions<RoselineMcpOptions>? options,
        string? project,
        WritePrompt prompt,
        CancellationToken cancellationToken)
    {
        // Nothing to ask, or the operator turned the confirmation off for this deployment
        // (RoselineMCP:ConfirmDestructiveWrites) — either way the explicit previewOnly: false
        // opt-in stands. Note this suppresses the elicitation entirely rather than auto-accepting
        // one, so a client that would decline is never given the chance to.
        // A client that never negotiated elicitation cannot be asked either, so the explicit opt-in
        // stands — the same answer the catch-all below has always produced when ElicitAsync threw
        // for this reason. Deciding it from the negotiated capability instead of from an exception
        // is what keeps the target from being resolved for a prompt that has nowhere to go: this is
        // the common shape of "no human is reachable", not the rare one.
        if (!CanAskHuman(server, options))
        {
            return (WriteConfirmation.Proceed, null);
        }

        // The confirmation gets its own clock. Think-time must not be charged against the analysis
        // budget (DefaultTimeout), but it has to be charged against something: an accepted-then-
        // unanswered elicitation used to block the tool call forever. Zero or less keeps that
        // unbounded behavior as an escape hatch. Outside DI (unit tests) `options` is null, where
        // the documented default applies rather than "no bound".
        var timeoutMs = options?.Value.ConfirmDestructiveWritesTimeout
            ?? RoselineMcpOptions.DefaultConfirmDestructiveWritesTimeoutMs;

        // Resolve the target and build the prompt BEFORE the try, and exactly once. Resolution can
        // throw when nothing resolves — and every catch below ends in WriteConfirmation.Proceed.
        // Inside the try, an unresolvable target would therefore be read as "this client cannot be
        // asked", and the write would go ahead on a question nobody was ever shown: the exact
        // inversion this gate exists to prevent. Out here it propagates to the tool's own handler
        // and becomes the error envelope, with no elicitation sent.
        var target = ResolveWriteTarget(project);
        var message = prompt.Render(target);

        using var elicitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
        {
            elicitCts.CancelAfter(timeoutMs);
        }

        try
        {
            // A field-less form: the user simply accepts or declines the confirmation prompt.
            var request = new ElicitRequestParams
            {
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema(),
            };
            var result = await server.ElicitAsync(request, elicitCts.Token);
            return (result.IsAccepted ? WriteConfirmation.Proceed : WriteConfirmation.Declined, target);
        }
        // OUR deadline fired, and the caller did not cancel: the client was asked and said
        // nothing. Silence is not consent — downgrade to a preview instead of writing. The test
        // is deliberately positive (this CTS, armed) rather than "not the caller": a client that
        // disconnects mid-prompt also cancels the round-trip without the caller's token moving,
        // and that is a broken session, not an unanswered question — it must keep propagating,
        // including when no deadline was ever armed (timeoutMs <= 0).
        //
        // This catches Exception and not OperationCanceledException on purpose. Cancelling an
        // in-flight request is not guaranteed to surface as an OCE: a JSON-RPC client library may
        // report the abandoned round-trip as a transport or protocol exception instead. Catching
        // only OCE here would drop those into the "elicitation unsupported" fallback below, which
        // returns Proceed — writing to disk on a confirmation nobody answered, the exact inversion
        // of what this gate exists to prevent. The filter, not the exception type, is what makes
        // this branch safe: it fires only when our own deadline is the reason the call failed.
        catch (Exception)
            when (timeoutMs > 0 && elicitCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return (WriteConfirmation.TimedOut, target);
        }
        catch (OperationCanceledException)
        {
            // A real request cancellation — or a cancelled session — still propagates, unchanged.
            throw;
        }
        catch (Exception)
        {
            // The round-trip failed for some reason other than our deadline — honor the explicit
            // opt-in, and keep the target we already resolved: the write still lands where the
            // prompt said it would.
            return (WriteConfirmation.Proceed, target);
        }
    }

    /// <summary>
    /// The note a write tool adds to its response when the confirmation gate downgraded the
    /// operation to a preview. Lives beside the gate so all three write tools word the two
    /// outcomes identically, and so a caller can tell "you said no" from "nobody answered".
    /// </summary>
    private static string WriteConfirmationNote(WriteConfirmation confirmation) => confirmation switch
    {
        WriteConfirmation.TimedOut =>
            "Write confirmation timed out; returned a preview only (no files were modified). Set "
            + "RoselineMCP:ConfirmDestructiveWrites=false on unattended hosts that should write "
            + "without a human, or raise RoselineMCP:ConfirmDestructiveWritesTimeout.",
        WriteConfirmation.Declined =>
            "Write declined via client confirmation; returned a preview only (no files were modified).",
        // Every arm is spelled out on purpose: a catch-all would quietly attach "declined" to a
        // Proceed — describing a write that DID happen as one that did not — and would swallow
        // any outcome added later instead of failing loudly here.
        _ => throw new ArgumentOutOfRangeException(
            nameof(confirmation), confirmation, "No note exists for this write-confirmation outcome."),
    };

    /// <summary>
    /// Whether a confirmation prompt can reach a human at all — no server, the operator switch off
    /// (<c>RoselineMCP:ConfirmDestructiveWrites</c>), or a client that never negotiated elicitation.
    /// </summary>
    /// <remarks>
    /// It lives here, beside the gate that acts on it, and both callers share this one expression.
    /// <see cref="ConfirmDestructiveWriteAsync"/> uses it to decide whether to ask;
    /// <see cref="RunVerifiedWriteAsync{T}"/> uses it to decide whether the two-phase flow is worth
    /// running at all. A second, hand-maintained copy of these conditions is precisely how the three
    /// write tools' prompts drifted apart before the gate was centralized.
    /// </remarks>
    private static bool CanAskHuman(McpServer? server, IOptions<RoselineMcpOptions>? options) =>
        server is not null
        && options?.Value.ConfirmDestructiveWrites != false
        && server.ClientCapabilities?.Elicitation is not null;

    /// <summary>
    /// The concrete <c>.sln</c>/<c>.csproj</c> path a write will land on — an absolute path, whether
    /// the caller passed a project, left it out, or passed an empty string. This is both what the
    /// confirmation prompt names and what the write is then performed against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It resolves through the very function the loader uses —
    /// <see cref="ProjectLoader.ResolveTargetPath"/> with the same base directory
    /// (<see cref="Directory.GetCurrentDirectory"/>, as <c>IProjectLoader.LoadAsync</c> passes it) —
    /// and the result is handed back to the caller to use as the <c>project</c> argument for the
    /// write itself. That last part is what actually makes prompt and write agree: resolving twice
    /// around a round-trip that may take minutes would let the file system change in between, so
    /// the human could approve one solution and a second resolution could pick another (or fail).
    /// Resolving once and carrying the answer forward removes the window rather than narrowing it.
    /// </para>
    /// <para>
    /// <see cref="Path.GetFullPath(string)"/> because <see cref="ProjectLoader.ResolveTargetPath"/>
    /// returns an existing <c>.csproj</c> argument verbatim, so a relative one would otherwise reach
    /// the prompt as typed — unreadable to a human who does not know the server's working directory,
    /// which is the one fact the prompt exists to expose. <c>CachingProjectLoader</c> normalizes the
    /// same value for its cache key.
    /// </para>
    /// <para>
    /// No MSBuild workspace is loaded, so this is far cheaper than the load that follows — but it is
    /// not free. A bare project name that matches neither a file nor a directory falls through to a
    /// recursive <c>*.csproj</c> scan of the working directory, which on a large tree is slow. That
    /// is the main reason every path which will not send a prompt returns before calling this. No
    /// discovery scan reached from here — this recursive sweep, or the auto-discovery walk taken
    /// when <paramref name="project"/> is omitted — throws on an unreadable directory it encounters
    /// any more; every one of them skips it (the shared <c>IncidentalScan</c> options,
    /// <c>IgnoreInaccessible</c>) — so cost, not fragility, is what remains of the justification.
    /// A <em>named</em> directory that cannot be read still throws, by design, and now surfaces as
    /// <c>AnalysisError</c> rather than a message-scrubbed <c>InternalError</c>.
    /// </para>
    /// <para>
    /// Two symptoms shared one cause here. A caller who omitted <c>project</c> — the documented
    /// default, since auto-discovery is the advertised behavior — was asked to authorise a write to
    /// the literal placeholder "the auto-discovered project"; and an empty string fell through a
    /// <c>??</c> to render <c>in ''</c>, because the prompt tested for null while the loader treats
    /// null <em>and whitespace</em> alike. Delegating removes both at once: there is no second
    /// opinion left to disagree with.
    /// </para>
    /// <para>
    /// Unresolvable targets throw rather than degrade to a placeholder, and that is deliberate.
    /// <see cref="ConfirmDestructiveWriteAsync"/> calls this outside its try, so the exception
    /// reaches the tool's own handler and becomes the standard error envelope with no elicitation
    /// sent: asking a human to approve a write that cannot even be targeted wastes their answer on
    /// a call that was going to fail anyway.
    /// </para>
    /// </remarks>
    private static string ResolveWriteTarget(string? project) =>
        Path.GetFullPath(ProjectLoader.ResolveTargetPath(project, Directory.GetCurrentDirectory()));

    /// <summary>
    /// Resolves whether a write tool should actually write. Applies the confirmation gate when the
    /// caller opted in with <c>previewOnly: false</c>, and returns the effective previewOnly flag
    /// plus, when the write was downgraded to a preview, the note to surface on the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One home for the whole write-gate policy — when to ask, what the answer means for this call,
    /// what to log, and what to tell the caller — so the three write tools cannot drift apart. They
    /// already did once: three hand-maintained copies of this block grew three different
    /// confirmation messages, one of which asked the human to approve a write "in ''". Only
    /// <paramref name="prompt"/> legitimately varies per tool — and since #161 it varies over a
    /// closed vocabulary (<see cref="WriteScope"/>) rather than over free text, so the words can no
    /// longer drift where the policy no longer can.
    /// </para>
    /// <para>
    /// <paramref name="prompt"/> carries the scope and the <em>values</em> — never a finished
    /// sentence. That shape is what makes the guarantee structural: a tool cannot forget to resolve,
    /// cannot resolve differently from its siblings, cannot interpolate the raw <c>project</c> back
    /// in — the bug this gate grew twice — and, since #161, cannot hand over caller input that has
    /// already been interpolated. The previous <c>Func&lt;string, string&gt;</c> had run the
    /// interpolation before the helper ever saw the string, so there was nothing left to escape: a
    /// crafted <c>symbol</c> closed the quoted run and appended a second, benign-looking sentence
    /// naming a project the write would never touch. Resolution stays lazy either way — nothing
    /// resolves on a <c>previewOnly: true</c> call, which has no use for the answer and would
    /// otherwise pay for directory discovery, or fail outright where discovery is ambiguous.
    /// </para>
    /// <para>
    /// The returned <c>ResolvedTarget</c> is the path the human was shown; callers pass it to the
    /// service in place of the caller's <c>project</c> so the write lands on precisely what was
    /// approved. It is <see langword="null"/> whenever no prompt was sent — a preview, a disabled
    /// gate, a client that cannot elicit — since nothing was resolved and nothing was approved.
    /// </para>
    /// <para>
    /// The human round-trip is bounded by <paramref name="cancellationToken"/> (the caller's request
    /// token) and by the confirmation's own clock
    /// (<see cref="RoselineMcpOptions.ConfirmDestructiveWritesTimeout"/>) — never by the wall-clock
    /// analysis budget: think-time must not be charged against it. Callers must therefore arm their
    /// analysis budget (<see cref="CreateLinkedTimeoutSource"/>) only <em>after</em> this returns.
    /// </para>
    /// </remarks>
    public static async Task<(bool PreviewOnly, string? Note, string? ResolvedTarget)> ResolveWriteModeAsync(
        McpServer? server,
        IOptions<RoselineMcpOptions>? options,
        bool previewOnly,
        string? project,
        WritePrompt prompt,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        // A preview never reaches disk, so there is nothing to confirm. This reproduces the
        // `!previewOnly` guard each call site used to carry, keeping the elicitation off the
        // read-only path entirely.
        if (previewOnly)
        {
            return (true, null, null);
        }

        var (confirmation, resolvedTarget) =
            await ConfirmDestructiveWriteAsync(server, options, project, prompt, cancellationToken);
        if (confirmation == WriteConfirmation.Proceed)
        {
            return (false, null, resolvedTarget);
        }

        logger?.LogWarning(
            "Write not confirmed ({Outcome}): returning a preview only, nothing was written to disk.",
            confirmation);

        return (true, WriteConfirmationNote(confirmation), resolvedTarget);
    }

    /// <summary>
    /// The verified-write flow every write tool follows: <b>verify, then ask, then write</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the whole point. Asking a human to approve a write the compile gate is about to
    /// refuse spends the one thing the elicitation costs — their attention — and trains them to
    /// click through it. A refusal is also strictly more informative than a decline: it carries the
    /// diff <em>and</em> the errors. The same reasoning applies one step earlier: a phase-1 response
    /// that carries no changes at all (<see cref="IWriteToolResponse.HasChanges"/> is <see
    /// langword="false"/>) is returned immediately after the refusal check, for the same reason —
    /// there is nothing for a human to approve.
    /// </para>
    /// <para>
    /// Phase 1 always runs as a preview, whatever the caller asked for, so nothing can reach disk
    /// before the verdict is in. Phase 2 runs only on an approved, verified write with real changes
    /// to make — and re-verifies, because the tree may have moved while the human was deciding.
    /// </para>
    /// <para>
    /// Each phase gets its own analysis budget and the confirmation between them is charged to
    /// neither: think-time belongs to <see cref="RoselineMcpOptions.ConfirmDestructiveWritesTimeout"/>,
    /// never to <see cref="RoselineMcpOptions.DefaultTimeout"/>. With the shipped defaults (120s
    /// analysis, 300s confirmation) a single budget spanning the prompt would already have expired,
    /// turning the documented preview into a TimeoutError the caller cannot act on.
    /// </para>
    /// </remarks>
    /// <param name="server">The MCP server used to elicit the confirmation; null disables the prompt.</param>
    /// <param name="options">Options carrying the confirmation switches and the analysis budget.</param>
    /// <param name="previewOnly">The caller's own request. When true, phase 2 never runs.</param>
    /// <param name="allowIntroducedErrors">When true, the compile gate reports but does not refuse.</param>
    /// <param name="project">The caller's project argument, passed through to phase 1.</param>
    /// <param name="prompt">The scope and values the confirmation sentence is rendered from.</param>
    /// <param name="execute">
    /// Runs the underlying service call for one phase: (project, previewOnly, reportProgress, token).
    /// <c>reportProgress</c> is true for exactly one phase per call, so a tool that emits MCP
    /// progress notifications never replays the sequence.
    /// </param>
    /// <param name="budget">The caller's per-phase analysis budget, so its catch block can still tell a timeout from a cancellation.</param>
    /// <param name="logger">Logger for the gate's own diagnostics.</param>
    /// <param name="cancellationToken">The request token — the only clock over the human round-trip.</param>
    public static async Task<T> RunVerifiedWriteAsync<T>(
        McpServer? server,
        IOptions<RoselineMcpOptions>? options,
        bool previewOnly,
        bool allowIntroducedErrors,
        string? project,
        WritePrompt prompt,
        Func<string?, bool, bool, CancellationToken, Task<T>> execute,
        AnalysisBudget budget,
        ILogger? logger,
        CancellationToken cancellationToken)
        where T : IWriteToolResponse
    {
        // When nobody can be asked — a preview, the operator switch off, or a client that cannot
        // elicit — there is no prompt to keep a refusal away from, and the service verifies and
        // refuses on its own regardless. Running phase 1 anyway would execute the whole operation
        // twice for no benefit, which on an unattended host (CI, headless agents) is the common
        // path, not the rare one.
        if (previewOnly || !CanAskHuman(server, options))
        {
            return await execute(project, previewOnly, true, budget.Start());
        }

        // PHASE 1 — build the candidate, diff it, and put it to the compiler. Never writes.
        // It carries the progress notifications: it is the long half, and it runs *before* the human
        // is asked, which is when a caller most wants to see something happening.
        var result = await execute(project, true, true, budget.Start());

        if (WasRefused(result, allowIntroducedErrors))
        {
            return result;
        }

        // A human is asked only once there is a valid, non-empty write to approve.
        if (!result.HasChanges)
        {
            return result;
        }

        // No budget is armed across the human round-trip.
        budget.Stop();

        // Gate policy AND wording both live in ResolveWriteModeAsync: the tool named a scope and
        // handed over its values, and the sentence is composed from them there (#161).
        var (effectivePreviewOnly, confirmationNote, writeTarget) = await ResolveWriteModeAsync(
            server, options, previewOnly, project, prompt, logger, cancellationToken);

        if (effectivePreviewOnly)
        {
            // Declined or timed out. Phase 1's response already holds the diff and the verdict, so
            // there is nothing left to compute.
            result.PreviewOnly = true;
            if (confirmationNote is not null)
            {
                result.Notes.Add(confirmationNote);
            }

            return result;
        }

        // PHASE 2 — the approved write, on a fresh budget, against the path the human was actually
        // shown (or the caller's own argument when nobody was asked, since nothing was resolved).
        //
        // Progress is deliberately NOT reported again: MCP requires progress values to increase
        // strictly per notification, and phase 2 repeats the same operation from the same starting
        // values — a client validating the sequence would drop or error on the replay.
        return await execute(writeTarget ?? project, false, false, budget.Start());
    }

    /// <summary>
    /// Whether the compile gate refused this change. Read off the verdict rather than a separate
    /// flag, so no tool can disagree with the service about what "refused" means.
    /// </summary>
    public static bool WasRefused(IWriteToolResponse result, bool allowIntroducedErrors) =>
        !allowIntroducedErrors && result.Verification?.Introduced is { Count: > 0 };

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

        // Outside DI (unit tests) this falls back to "no budget", where
        // ConfirmDestructiveWriteAsync falls back to the shipped default instead. The asymmetry is
        // deliberate, not an oversight: each side errs toward the safe answer for its own concern.
        // An unbounded analysis in a test costs nothing, whereas an unbounded confirmation would
        // remove the very bound that stops silence being read as consent.
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
        CancellationTokenSource? timeoutSource,
        IOptions<RoselineMcpOptions>? options,
        string correlationId)
    {
        // A null source means the wall-clock budget had not started yet — the call was still in
        // the write confirmation — so a cancellation there can only be the caller's own.
        if (!requestToken.IsCancellationRequested && timeoutSource?.IsCancellationRequested == true)
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
    /// <remarks>
    /// <para>
    /// <see cref="UnauthorizedAccessException"/> sits in the analysis arm alongside
    /// <see cref="IOException"/>, and it has to be named explicitly: it derives from
    /// <c>SystemException</c>, <em>not</em> from <see cref="IOException"/>, so without this it
    /// falls to the catch-all — the one arm that scrubs the message. A permission failure is an
    /// analysis-tier failure because it happens while reading, enumerating, or writing the target
    /// (<c>ProjectLoader</c>'s discovery scans, <c>SourceTextWriter.WriteAsync</c>), which is what
    /// <c>docs/API.md</c> defines <c>AnalysisError</c> to mean. It is not
    /// <see cref="ToolErrorTypes.Validation"/> — the caller's arguments were fine — and not
    /// <see cref="ToolErrorTypes.NotFound"/> — the path exists, it just cannot be read or written.
    /// Classifying it here is what lets "Access to the path '...' is denied." reach the caller
    /// intact, so a permission problem can be fixed instead of reported as an opaque correlation id.
    /// </para>
    /// </remarks>
    private static string Classify(Exception ex) => ex switch
    {
        ArgumentException or FormatException => ToolErrorTypes.Validation,
        FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => ToolErrorTypes.NotFound,
        InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException => ToolErrorTypes.Analysis,
        _ => ToolErrorTypes.Internal
    };
}

/// <summary>
/// The analysis wall-clock budget for one tool call, re-armed per phase.
/// </summary>
/// <remarks>
/// A verified write runs in two phases with a human confirmation between them. Think-time must not
/// be charged against analysis time — that is what the confirmation's own clock is for — so each
/// phase gets a fresh budget and the gap between them is timed by neither. <see cref="Current"/> is
/// exposed so a tool's catch block can still distinguish a wall-clock timeout from the caller
/// cancelling.
/// </remarks>
public sealed class AnalysisBudget : IDisposable
{
    private readonly CancellationToken _requestToken;
    private readonly IOptions<RoselineMcpOptions>? _options;

    /// <summary>Creates a budget bound to the request token and the configured DefaultTimeout.</summary>
    public AnalysisBudget(CancellationToken requestToken, IOptions<RoselineMcpOptions>? options)
    {
        _requestToken = requestToken;
        _options = options;
    }

    /// <summary>The budget in force, or <see langword="null"/> before the first phase and between phases.</summary>
    public CancellationTokenSource? Current { get; private set; }

    /// <summary>Retires any running budget and starts a fresh one for the next phase.</summary>
    public CancellationToken Start()
    {
        Stop();
        Current = ToolExecutionHelper.CreateLinkedTimeoutSource(_requestToken, _options);
        return Current.Token;
    }

    /// <summary>Retires the current budget: nothing is being timed until the next <see cref="Start"/>.</summary>
    public void Stop()
    {
        Current?.Dispose();
        Current = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
