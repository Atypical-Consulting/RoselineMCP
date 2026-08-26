using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Drives the write-path elicitation gate end-to-end through a real client/server pair: when a tool
/// is called with <c>previewOnly: false</c>, the server elicits a confirmation from the client
/// before writing. A declining client downgrades the operation to a preview; an accepting client
/// lets the write proceed. Clients without an elicitation handler are covered by the rest of the
/// protocol suite (the gate falls back to honoring the explicit opt-in). A third case covers the
/// operator switch <c>RoselineMCP:ConfirmDestructiveWrites=false</c>: an elicitation-capable client
/// that would decline is deliberately never asked, so the write stands — asserted for both
/// <c>apply_fixes</c> and <c>rename_symbol</c>, since the gate lives in the shared helper.
/// <c>edit_member</c> gets a decline case of its own so all three write tools are covered
/// end-to-end.
///
/// A final group pins the <em>content</em> of the prompts rather than the answer. A confirmation
/// whose target is a placeholder cannot be answered correctly however well-gated it is, so each
/// message must name the concrete <c>.sln</c>/<c>.csproj</c> that will be written — when the caller
/// passes a directory, when they omit <c>project</c> entirely, and when they pass an empty string.
/// Two boundary cases sit either side of that: a target that resolves to nothing fails before a
/// human is ever asked, and a <c>previewOnly</c> call never builds the message at all, since naming
/// the target costs a resolution a read-only call has no use for.
/// </summary>
[Collection(McpProtocolCollection.Name)]
public class ElicitationTests : IDisposable
{
    /// <summary>
    /// A throwaway directory holding one real <c>.csproj</c>. The confirmation prompt now names the
    /// concrete project it will write to, which means resolving the caller's <c>project</c> argument
    /// against the file system — so every case that expects to be <em>asked</em> has to point at
    /// something that actually resolves. A bare name like "TestProject" no longer does, and is kept
    /// deliberately in the two cases that must never resolve at all (see below).
    /// </summary>
    private readonly string _fixtureRoot;

    /// <summary>The fixture <c>.csproj</c> itself — the path a prompt naming this target must contain.</summary>
    private readonly string _fixtureProject;

    /// <summary>
    /// A <c>.sln</c> beside the fixture project, for the cases that need the resolved write target
    /// to be a <em>solution</em>. It only ever has to exist: <c>ResolveTargetPath</c> returns an
    /// existing <c>.sln</c> argument verbatim, and the prompt is built from that path alone — no
    /// MSBuild load happens before the human is asked, which is the property those cases pin.
    /// Passing the directory instead would resolve to the <c>.csproj</c> (<c>ResolveProjectPath</c>
    /// globs <c>*.csproj</c>), so the solution branch has to be targeted explicitly.
    /// </summary>
    private readonly string _fixtureSolution;

    public ElicitationTests()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), $"RoselineElicitation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixtureRoot);
        _fixtureProject = Path.Combine(_fixtureRoot, "Fixture.csproj");
        File.WriteAllText(
            _fixtureProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        _fixtureSolution = Path.Combine(_fixtureRoot, "Fixture.sln");
        File.WriteAllText(_fixtureSolution, "Microsoft Visual Studio Solution File, Format Version 12.00\n");
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_fixtureRoot, true); }
        catch { /* ignored */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The write target named by a confirmation prompt — the last quoted segment, which is where
    /// all three messages put it ("… anywhere in the code loaded from '&lt;target&gt;'."). Reading
    /// it out of the message, rather than re-deriving the expected path, is what keeps these
    /// assertions from re-implementing the resolution they are supposed to be checking.
    /// </summary>
    /// <remarks>
    /// Derived from two invariants rather than guessed at. #173 made the target the LAST thing in
    /// every sentence, so its <em>closing</em> quote is always the message's last apostrophe — that
    /// half was already exact. What used to be ambiguous is the <em>opening</em> quote, because a
    /// resolved path may itself contain an apostrophe (<c>C:\Users\O'Brien\src</c>,
    /// <c>~/Bob's Projects</c>) and the target is the one value <see cref="WritePrompt.Sanitize"/>
    /// deliberately does not touch — so it is also the only quoted run that can contain one. Every
    /// <em>other</em> quoted run in the sentence wraps a <c>Sanitize</c>d caller value, and
    /// <c>Sanitize</c>'s whitelist admits neither an apostrophe nor a space, so each of those runs is
    /// a clean, self-contained pair and always contributes an <em>even</em> number of quotes to the
    /// message — regardless of how many precede the target for a given <see cref="WriteScope"/>
    /// (zero for <c>apply_fixes</c>, two for <c>edit_member</c> and <c>rename_symbol</c>). That means
    /// the <em>parity</em> of the message's total quote count says, on its own and without knowing
    /// the scope or the frame's wording, whether the target's run swallowed one such internal
    /// apostrophe: an even total means it holds none, so its opening quote is the one immediately
    /// before the close; an odd total means it holds one, so the real opening quote sits one quote
    /// further back still. This replaces the old "the opening quote is the last one preceded by a
    /// space" guess, which read an apostrophe as opening the target's run whenever it happened to
    /// follow a space too — mis-parsing a target like <c>/repo/x 'y/App.sln</c> into a truncated
    /// tail (#204).
    /// </remarks>
    private static string TargetFromPrompt(string message)
    {
        var close = message.LastIndexOf('\'');
        close.ShouldBeGreaterThanOrEqualTo(0, $"the prompt names no target at all: {message}");

        var totalQuotes = message.Count(c => c == '\'');
        var beforeClose = close > 0 ? message.LastIndexOf('\'', close - 1) : -1;
        var open = totalQuotes % 2 == 0
            ? beforeClose
            : (beforeClose > 0 ? message.LastIndexOf('\'', beforeClose - 1) : -1);

        open.ShouldBeGreaterThanOrEqualTo(0, $"the prompt's target quoting is unbalanced: {message}");
        close.ShouldBeGreaterThan(open, $"the prompt's target quoting is unbalanced: {message}");
        return message[(open + 1)..close];
    }

    /// <summary>
    /// Asserts a prompt names a concrete solution/project that exists on disk — the whole point of
    /// the confirmation. Deliberately makes no claim about <em>which</em> one auto-discovery picks:
    /// that depends on the working directory the suite happens to run from, and pinning it would
    /// test the runner rather than the prompt.
    /// </summary>
    private static void ShouldNameARealProject(string message)
    {
        var target = TargetFromPrompt(message);
        Path.IsPathRooted(target).ShouldBeTrue($"the prompt must name an absolute path, not '{target}'");
        File.Exists(target).ShouldBeTrue($"the prompt names '{target}', which is not on disk");
        Path.GetExtension(target).ToLowerInvariant()
            .ShouldBeOneOf(".sln", ".csproj");
    }

    private static Task<McpProtocolTestHost> StartHostAsync(
        ICodeFixService codeFixService,
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> elicitationHandler,
        Action<RoselineMcpOptions>? configureOptions = null,
        ICodeEditService? editService = null)
        => McpProtocolTestHost.StartAsync(
            services =>
            {
                services.AddSingleton(codeFixService);
                services.AddSingleton(A.Fake<ISolutionAnalyzerService>());
                services.AddSingleton(A.Fake<ICodeNavigationService>());
                services.AddSingleton(editService ?? A.Fake<ICodeEditService>());
                services.AddSingleton<IDiffService, DiffService>();
                services.AddSingleton<IPatchService, PatchService>();
            },
            elicitationHandler,
            configureOptions);

    private static ICodeFixService FakeCodeFixCapturingPreviewOnly(Action<bool> capture)
    {
        var codeFix = A.Fake<ICodeFixService>();
        A.CallTo(() => codeFix.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, List<string> _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                capture(previewOnly))
            .ReturnsLazily((string _, List<string> _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly, ChangedFiles = { "Fixture.cs" } }));
        return codeFix;
    }

    [Fact]
    public async Task ApplyFixes_With_PreviewOnly_False_Is_Downgraded_To_Preview_When_Client_Declines()
    {
        bool? captured = null;
        var elicited = false;
        var codeFix = FakeCodeFixCapturingPreviewOnly(p => captured = p);

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => { elicited = true; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); });

        var result = await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["ids"] = new[] { "RCS1213" },
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The client was actually asked to confirm, and the declined write was downgraded to a
        // preview: the service was invoked in preview mode and the response says so.
        elicited.ShouldBeTrue();
        captured.ShouldBe(true);

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetString()).ShouldContain(s => s!.Contains("declined"));
    }

    [Fact]
    public async Task ApplyFixes_With_PreviewOnly_False_Writes_When_Client_Accepts()
    {
        bool? captured = null;
        var codeFix = FakeCodeFixCapturingPreviewOnly(p => captured = p);

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }));

        await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["ids"] = new[] { "RCS1213" },
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Accepted → the write proceeds; the service was invoked with previewOnly = false.
        captured.ShouldBe(false);
    }

    [Fact]
    public async Task ApplyFixes_With_PreviewOnly_False_Is_Downgraded_To_Preview_When_Confirmation_Times_Out()
    {
        bool? captured = null;
        var elicited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codeFix = FakeCodeFixCapturingPreviewOnly(p => captured = p);

        // A client that advertises elicitation support, accepts the request, and then never
        // answers - the unattended-host case. Before the round-trip was bounded this wedged the
        // tool call forever: RoselineMCP:DefaultTimeout does not apply to the confirmation by
        // construction, so nothing else could ever end the wait.
        var neverAnswers = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int ConfirmTimeoutMs = 200;

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) =>
            {
                elicited.TrySetResult(true);
                return new ValueTask<ElicitResult>(neverAnswers.Task);
            },
            options => options.ConfirmDestructiveWritesTimeout = ConfirmTimeoutMs);

        // A local async function, so the bounded wait below works whether the SDK hands back a
        // Task or a ValueTask.
        async Task<CallToolResult> CallApplyFixesAsync() =>
            await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
            {
                ["project"] = _fixtureProject,
                ["ids"] = new[] { "RCS1213" },
                ["previewOnly"] = false,
            });

        var call = CallApplyFixesAsync();

        // Release the client's handler well after the confirmation's own deadline should have
        // fired, and unconditionally after a ceiling even if the prompt never arrives. The
        // ordering matters twice over. The SDK's own McpClient dispatches server-initiated
        // requests on its single read loop, so a handler that never returns also stops the client
        // reading the tool response the server has already written; and parking the handler
        // forever would leave `await using host` unable to drain that read loop, wedging the whole
        // (DisableParallelization) protocol collection instead of reporting the regression.
        //
        // The trigger is the prompt being *sent*, not the fix service running: since verification
        // was added, the service runs once BEFORE the confirmation (in preview, to get the
        // compiler's verdict), so "the service was called with previewOnly: true" no longer marks
        // the downgrade. The delay is derived from the configured deadline rather than hardcoded.
        //
        // A late "decline" is also what makes the assertion sharp: had the wait been unbounded,
        // the server would have taken that answer and the note below would read "declined".
        var release = Task.Run(async () =>
        {
            await Task.WhenAny(elicited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            await Task.Delay(ConfirmTimeoutMs * 8);
            neverAnswers.TrySetResult(new ElicitResult { Action = "decline" });
        }, TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(15)));
        finished.ShouldBeSameAs(call, "the tool call did not return after the confirmation timed out");

        var result = await call;
        await release;
        captured.ShouldBe(true);

        // The note names the timeout specifically: a caller must be able to tell "you said no"
        // from "nobody answered".
        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetString()).ShouldContain(s => s!.Contains("timed out"));
    }

    [Fact]
    public async Task ApplyFixes_Confirmation_Timeout_Still_Returns_A_Preview_When_DefaultTimeout_Is_Shorter()
    {
        // The shipped defaults put DefaultTimeout (120 s) BELOW ConfirmDestructiveWritesTimeout
        // (5 min). So the analysis clock must not be armed while the human is being asked: if it
        // is, it expires long before the confirmation gives up, and the token handed to the
        // service is already cancelled by the time the gate downgrades the call — the caller then
        // gets a TimeoutError envelope instead of the preview-and-note that docs/API.md,
        // SECURITY.md and the CHANGELOG all promise. This reproduces that ordering two orders of
        // magnitude faster: analysis budget 1 s, confirmation budget 2.5 s.
        //
        // Only the ORDERING is load-bearing, so the analysis budget is given real margin rather
        // than the tightest value that still orders correctly. Once the gate downgrades the call,
        // the fake's first statement is ThrowIfCancellationRequested — with a budget of a few
        // hundred milliseconds, one GC pause between arming the clock and reaching that check
        // cancels the token and the test fails with the exact symptom it is guarding against,
        // reading as a real regression.
        bool? captured = null;

        var codeFix = A.Fake<ICodeFixService>();
        A.CallTo(() => codeFix.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, List<string> _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken ct) =>
            {
                // The real service checks the token before doing anything, so a fake that ignores
                // it cannot see an analysis budget that already expired — which is precisely the
                // failure being guarded against here.
                ct.ThrowIfCancellationRequested();
                captured = previewOnly;
                return Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly, ChangedFiles = { "Fixture.cs" } });
            });

        var neverAnswers = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elicited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int ConfirmTimeoutMs = 2500;

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) =>
            {
                elicited.TrySetResult(true);
                return new ValueTask<ElicitResult>(neverAnswers.Task);
            },
            options =>
            {
                options.DefaultTimeout = 1000;
                options.ConfirmDestructiveWritesTimeout = ConfirmTimeoutMs;
            });

        // The SDK's McpClient dispatches server-initiated requests on its single read loop, so the
        // pending handler has to be released before the client can read the tool response the
        // server already wrote. Keyed off the prompt being sent, then held past the confirmation's
        // own deadline: since verification was added, the fix service runs once BEFORE the
        // confirmation (in preview, to get the compiler's verdict), so "the service ran" no longer
        // marks the gate giving up. The delay is derived from the deadline configured above rather
        // than hardcoded, and the ceiling still releases the handler if no prompt ever arrives, so
        // a regression fails this test instead of wedging the collection.
        //
        // A late "decline" is also what makes the assertion sharp: had the wait been unbounded,
        // the server would have taken that answer and the note would read "declined" rather than
        // "timed out".
        var release = Task.Run(async () =>
        {
            await Task.WhenAny(elicited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            await Task.Delay(ConfirmTimeoutMs * 3);
            neverAnswers.TrySetResult(new ElicitResult { Action = "decline" });
        }, TestContext.Current.CancellationToken);

        async Task<CallToolResult> CallApplyFixesAsync() =>
            await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
            {
                ["project"] = _fixtureProject,
                ["ids"] = new[] { "RCS1213" },
                ["previewOnly"] = false,
            });

        var call = CallApplyFixesAsync();
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(20)));
        finished.ShouldBeSameAs(call, "the tool call did not return after the confirmation timed out");

        var result = await call;
        await release;

        // The service ran — with an analysis budget that had NOT already been spent on think-time.
        captured.ShouldBe(true);

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("ok").GetBoolean()
            .ShouldBeTrue("the confirmation timeout must produce a preview, not a failure envelope");
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetString()).ShouldContain(s => s!.Contains("timed out"));
    }

    [Fact]
    public async Task ApplyFixes_With_PreviewOnly_False_Skips_Elicitation_When_Confirmation_Is_Disabled()
    {
        bool? captured = null;
        var elicited = false;
        var codeFix = FakeCodeFixCapturingPreviewOnly(p => captured = p);

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => { elicited = true; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            options => options.ConfirmDestructiveWrites = false);

        var result = await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = "TestProject",
            ["ids"] = new[] { "RCS1213" },
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The gate is off, so the client is never asked — its decline never happens and the write
        // stands. Note the client here DOES advertise elicitation support: this proves the option
        // suppresses the request itself rather than merely auto-accepting an answer.
        //
        // `project` is deliberately left as a name that resolves to nothing. Building the prompt
        // resolves the target, so an operator who switched the gate off must not be made to pay
        // for — or fail on — a question that is never asked. Were resolution to creep back in
        // ahead of that switch, this call would come back as a failure envelope instead.
        elicited.ShouldBeFalse();
        captured.ShouldBe(false);

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task RenameSymbol_With_PreviewOnly_False_Is_Downgraded_To_Preview_When_Client_Declines()
    {
        // The write-path gate is shared by all three write tools; this exercises it via a second
        // tool (rename_symbol) to prove it is not specific to apply_fixes.
        bool? captured = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }),
            editService: edit);

        await host.Client.CallToolAsync("rename_symbol", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo",
            ["newName"] = "Bar",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        captured.ShouldBe(true);
    }

    [Fact]
    public async Task RenameSymbol_With_PreviewOnly_False_Skips_Elicitation_When_Confirmation_Is_Disabled()
    {
        // The disabled gate lives in the shared helper, not in apply_fixes: prove it through a
        // second tool, mirroring the decline-path test above — including the unresolvable
        // `project`, which pins that a suppressed prompt is never built and so never resolved.
        bool? captured = null;
        var elicited = false;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => { elicited = true; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            options => options.ConfirmDestructiveWrites = false,
            edit);

        await host.Client.CallToolAsync("rename_symbol", new Dictionary<string, object?>
        {
            ["project"] = "Demo",
            ["symbol"] = "Foo",
            ["newName"] = "Bar",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        elicited.ShouldBeFalse();
        captured.ShouldBe(false);
    }

    [Fact]
    public async Task EditMember_With_PreviewOnly_False_Is_Downgraded_To_Preview_When_Client_Declines()
    {
        // The third write tool's own end-to-end decline path. apply_fixes and rename_symbol each
        // have one; without this, edit_member could pass the caller's raw previewOnly straight to
        // the service — writing on a confirmation the human declined — with the suite still green.
        bool? captured = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }),
            editService: edit);

        var result = await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        captured.ShouldBe(true);

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetString()).ShouldContain(s => s!.Contains("declined"));
    }

    [Fact]
    public async Task EditMember_Refused_By_Verification_Never_Asks_For_Confirmation()
    {
        // Ordering, not politeness. Asking a human to approve a write the compile gate is about to
        // refuse spends the one thing the elicitation costs — their attention — and trains them to
        // click through it. A refusal is also strictly more informative than a decline: it carries
        // the diff *and* the errors.
        var elicited = false;
        var writeAttempted = false;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
            {
                if (!previewOnly)
                {
                    writeAttempted = true;
                }
            })
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse
                {
                    PreviewOnly = previewOnly,
                    Verification = new VerificationVerdict
                    {
                        Compiles = false,
                        Introduced = [new DiagnosticDetail { Id = "CS0103", File = "src/A.cs", Line = 7, Severity = "error" }]
                    }
                }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) =>
            {
                elicited = true;
                return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" });
            },
            editService: edit);

        var result = await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        elicited.ShouldBeFalse("a refused edit must never reach the human confirmation");
        writeAttempted.ShouldBeFalse("a refused edit must never reach the write pass");

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("ok").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("applied").GetBoolean().ShouldBeFalse();
        payload.GetProperty("data").GetProperty("verification").GetProperty("introduced")
            .EnumerateArray().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task EditMember_Verified_Clean_Still_Asks_For_Confirmation_And_Writes()
    {
        // The other half of the ordering: a change the compiler is happy with must still go through
        // the human gate. Verification narrows what gets asked about; it does not replace the ask.
        var elicited = false;
        var writeAttempted = false;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
            {
                if (!previewOnly)
                {
                    writeAttempted = true;
                }
            })
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse
                {
                    PreviewOnly = previewOnly,
                    Applied = !previewOnly,
                    ChangedFiles = { "src/Foo.cs" },
                    Verification = new VerificationVerdict { Compiles = true, ScopeComplete = true }
                }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) =>
            {
                elicited = true;
                return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" });
            },
            editService: edit);

        var result = await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        elicited.ShouldBeTrue("a clean edit still needs the human confirmation");
        writeAttempted.ShouldBeTrue();

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("applied").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_Write_Nobody_Can_Be_Asked_About_Runs_The_Operation_Once()
    {
        // The verified-write flow runs the operation twice when a human sits between verification
        // and the write. With the operator switch off there is no human, no prompt to keep a
        // refusal away from, and the service verifies and refuses on its own — so a second pass
        // would be pure duplicated work on exactly the unattended hosts (CI, headless agents) that
        // switch exists for.
        var calls = 0;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref calls))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, Applied = !previewOnly }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }),
            options => options.ConfirmDestructiveWrites = false,
            editService: edit);

        var result = await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        calls.ShouldBe(1);

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("applied").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_Preview_Runs_The_Operation_Once()
    {
        var calls = 0;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref calls))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }),
            editService: edit);

        await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
        }, cancellationToken: TestContext.Current.CancellationToken);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Change_Less_Write_Does_Not_Elicit()
    {
        // The root of #162: WasRefused reads the compiler's verdict, not the changed-file list, so
        // a phase-1 response that produced no changes at all was not a "refusal" and fell straight
        // through to the confirmation prompt — asking a human to approve a write that was never
        // going to happen. The gate must return phase 1's response before eliciting whenever it
        // carries no changes.
        var elicited = false;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse
                {
                    PreviewOnly = previewOnly,
                    Notes = { "No changes were produced by the edit." }
                }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => { elicited = true; return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); },
            editService: edit);

        var result = await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "replace",
            ["newSource"] = "public int Bar { get; set; }",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        elicited.ShouldBeFalse("a write that changes nothing has nothing for a human to approve");

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetString()).ShouldContain(s => s!.Contains("No changes were produced"));
    }

    [Fact]
    public async Task A_Write_With_Changes_Still_Elicits_And_Writes()
    {
        // The mirror negative: a response that DOES carry changes must still go through the human
        // gate exactly as before — HasChanges narrows what gets asked about, it does not remove
        // the ask.
        var elicited = false;
        var writeAttempted = false;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
            {
                if (!previewOnly)
                {
                    writeAttempted = true;
                }
            })
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse
                {
                    PreviewOnly = previewOnly,
                    Applied = !previewOnly,
                    ChangedFiles = { "src/Foo.cs" }
                }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) =>
            {
                elicited = true;
                return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" });
            },
            editService: edit);

        var result = await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["operation"] = "replace",
            ["newSource"] = "public int Bar { get; set; }",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        elicited.ShouldBeTrue("a write that carries real changes still needs a human's approval");
        writeAttempted.ShouldBeTrue();

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("applied").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task RenameSymbol_Reports_Progress_For_Exactly_One_Phase()
    {
        // MCP requires progress values to increase strictly per notification, and the confirmed
        // write path runs the rename twice. Replaying the sink would emit 1,2,3,1,2,3 — a sequence
        // a validating client drops or errors on. Exactly one phase may report.
        var progressPhases = 0;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._,
                A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, bool _, bool _, int _,
                      IProgress<ProgressNotificationValue>? progress, CancellationToken _) =>
            {
                if (progress is not null)
                {
                    Interlocked.Increment(ref progressPhases);
                }
            })
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _,
                            IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, Applied = !previewOnly }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }),
            editService: edit);

        await host.Client.CallToolAsync("rename_symbol", new Dictionary<string, object?>
        {
            ["project"] = _fixtureProject,
            ["symbol"] = "Foo.Bar",
            ["newName"] = "Baz",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        progressPhases.ShouldBe(1);
    }

    [Fact]
    public async Task Write_Confirmation_Names_The_Resolved_Project_Path()
    {
        // The caller passes the DIRECTORY holding the fixture project — one of the aliases
        // IProjectLoader accepts. The prompt must name what will actually be written, so it has to
        // show the resolved '.csproj', not the argument it was handed. Echoing the argument back is
        // precisely the old behavior, so a test that passed the .csproj itself would pass before
        // the fix and prove nothing.
        string? message = null;
        var codeFix = FakeCodeFixCapturingPreviewOnly(_ => { });

        await using var host = await StartHostAsync(
            codeFix,
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); });

        await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = _fixtureRoot,
            ["ids"] = new[] { "RCS1213" },
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message.ShouldContain(_fixtureProject);
    }

    [Fact]
    public async Task ApplyFixes_Confirmation_Names_The_Primary_Project_When_The_Target_Is_A_Solution()
    {
        // ApplyFixes is a PROJECT-scoped tool whose resolved write target may be a SOLUTION:
        // CodeFixService narrows it to one project (ProjectLoader.SelectPrimaryProject) and fixes
        // only that project's documents. A prompt naming the .sln therefore describes a broader
        // scope than the write actually has — a human reading "The write reaches '/repo/Acme.sln'"
        // cannot tell that two of their three projects will be untouched. The sentence has to say
        // so, which is what the "the primary project of" qualifier below is for.
        string? message = null;
        var codeFix = FakeCodeFixCapturingPreviewOnly(_ => { });

        await using var host = await StartHostAsync(
            codeFix,
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); });

        await host.Client.CallToolAsync(
            "apply_fixes",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["ids"] = new[] { "RCS1213" },
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message.ShouldContain("primary project of");
        message.ShouldContain(_fixtureSolution);

        // The scope qualifier must not cost the absolute-path guarantee the other prompt shapes
        // have: ResolveTargetPath returns an existing .sln argument verbatim, so normalization is
        // the only thing making a relative one readable, and this is the branch that pins it.
        ShouldNameARealProject(message);
    }

    [Fact]
    public async Task ApplyFixes_Confirmation_Names_The_Project_Exactly_When_The_Target_Is_A_Csproj()
    {
        // The other side of the branch, and the reason it is asserted byte-for-byte rather than by
        // substring: when the resolved target is already the project that gets written, "the
        // project" is exact and "the primary project of" would be a fresh inaccuracy — narrowing the
        // scope to a project-of-a-project that does not exist. A test that only checked for the
        // .csproj path would still pass if the wrong qualifier fired on both branches.
        string? message = null;
        var codeFix = FakeCodeFixCapturingPreviewOnly(_ => { });

        await using var host = await StartHostAsync(
            codeFix,
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); });

        // The directory alias resolves to the fixture .csproj — see ResolveProjectPath.
        await host.Client.CallToolAsync(
            "apply_fixes",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureRoot,
                ["ids"] = new[] { "RCS1213" },
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message.ShouldBe(
            "Apply code fixes for 1 diagnostic ID(s) and write the changes to disk? "
            + $"The write reaches the project '{_fixtureProject}'.");
        message.ShouldNotContain("primary project of");
    }

    [Fact]
    public async Task EditMember_Confirmation_Names_The_Single_File_Scope_When_The_Target_Is_A_Solution()
    {
        // EditMember is the widest of the three gaps: CodeEditService resolves ONE document and
        // calls SourceTextWriter.WriteAsync once, so a prompt naming a solution of N projects
        // describes a write that touches a single file in one of them. ApplyFixes at least writes a
        // whole project (#149); this one overstates by the entire solution.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync(
            "edit_member",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["symbol"] = "Foo.Bar",
                ["operation"] = "delete",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message.ShouldBe(
            "Write the 'delete' of member 'Foo.Bar' to disk? Exactly one file is rewritten — the "
            + $"declaration it resolves to, anywhere in the code loaded from '{_fixtureSolution}'.");
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("add")]
    [InlineData("delete")]
    public async Task EditMember_Confirmation_Names_The_Single_File_Scope_For_Every_Operation_And_A_Csproj_Target(string operation)
    {
        // Two properties in one, both of which a substring assertion would miss.
        //
        // 1. The scope clause does NOT branch on the target's extension, unlike ApplyFixes' (#149).
        //    ApplyFixes' scope depends on it — a .csproj target *is* its whole write scope — so its
        //    wording branches. EditMember writes one file whatever the target is, so a .csproj
        //    target gets the same clause a .sln does, and it still says "loaded from" rather than
        //    "in": a .csproj does not bound the write either, since ProjectLoader opens the
        //    containing solution and resolution spans every project in it.
        // 2. It DOES branch on the operation, and only on the noun. 'add' resolves `symbol` as the
        //    container type (CodeEditService.AddMember rejects anything else), so the prompt names a
        //    type rather than calling it a member that does not exist yet.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        // The directory alias resolves to the fixture .csproj — see ResolveProjectPath.
        await host.Client.CallToolAsync(
            "edit_member",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureRoot,
                ["symbol"] = "Foo.Bar",
                ["operation"] = operation,
                ["newSource"] = "public int Bar { get; set; }",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        var subject = operation == "add" ? "a member to type 'Foo.Bar'" : "member 'Foo.Bar'";
        message.ShouldBe(
            $"Write the '{operation}' of {subject} to disk? Exactly one file is rewritten — the "
            + $"declaration it resolves to, anywhere in the code loaded from '{_fixtureProject}'.");

        // "in '<target>'" would be the false claim this wording exists to avoid — see
        // CodeEditService_Writes_One_File_Which_May_Be_Outside_The_Named_Project.
        message.ShouldNotContain($"in '{_fixtureProject}'");
    }

    [Fact]
    public async Task RenameSymbol_Confirmation_Names_The_Solution_Without_A_Narrowing_Qualifier()
    {
        // The counterweight to the two above, and the reason they are not a blanket rule.
        // RenameSymbolAsync really is solution-wide — Renamer.RenameSymbolAsync, then every changed
        // project, then every changed file — so naming the solution is exact and a "single file"
        // qualifier here would be a fresh inaccuracy of the same family this issue is closing.
        // Asserted byte-for-byte: a substring check would not notice the qualifier leaking in.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync(
            "rename_symbol",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["symbol"] = "Foo",
                ["newName"] = "Bar",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message.ShouldBe(
            "Rename 'Foo' to 'Bar' and write the changes to disk? "
            + $"The write can reach any project in the solution of '{_fixtureSolution}'.");
        message.ShouldNotContain("Exactly one file");
    }

    [Fact]
    public async Task Approved_Write_Goes_To_The_Exact_Path_The_Human_Was_Shown()
    {
        // Naming the target is only half the guarantee. If the tool then hands the service the
        // caller's original argument, that argument is resolved a SECOND time — after a round-trip
        // the gate allows up to ConfirmDestructiveWritesTimeout (5 minutes by default) to complete.
        // A build, a checkout or a generator dropping a sibling .sln in that window is enough for
        // the second resolution to pick a different target, or to fail as ambiguous, and the human
        // approved neither. Resolving once and carrying the answer forward closes the window rather
        // than narrowing it, which is what docs/API.md and SECURITY.md actually claim.
        string? message = null;
        string? projectSeenByService = null;

        var codeFix = A.Fake<ICodeFixService>();
        A.CallTo(() => codeFix.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string project, List<string> _, bool _, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                projectSeenByService = project)
            .ReturnsLazily((string _, List<string> _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly, ChangedFiles = { "Fixture.cs" } }));

        await using var host = await StartHostAsync(
            codeFix,
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); });

        // The directory alias again: what the caller passes and what gets written are different
        // strings, so "the service saw the approved path" is a claim with teeth.
        await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = _fixtureRoot,
            ["ids"] = new[] { "RCS1213" },
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        projectSeenByService.ShouldBe(TargetFromPrompt(message));
        projectSeenByService.ShouldBe(_fixtureProject);
    }

    [Fact]
    public async Task Write_Confirmation_Names_An_Absolute_Path_For_A_Relative_Project()
    {
        // ProjectLoader.ResolveTargetPath returns an existing .csproj argument verbatim, so a
        // relative one would reach the prompt exactly as typed. A human cannot evaluate
        // '..\\src\\Acme.csproj' without knowing the server's working directory — which is the one
        // fact the prompt exists to expose, and the reason docs/API.md promises an absolute path.
        //
        // This fixture lives UNDER the working directory rather than in _fixtureRoot, because a
        // relative path has to be expressible at all: on a Windows CI runner the system temp
        // directory sits on a different volume from the checkout (C:\ vs D:\), and
        // Path.GetRelativePath then returns its input unchanged — there is no relative form across
        // drives, so the case being tested would silently not be exercised.
        var directory = Path.Combine(Directory.GetCurrentDirectory(), $"RoselineRelative_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var csproj = Path.Combine(directory, "Relative.csproj");
            await File.WriteAllTextAsync(
                csproj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);

            var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), csproj);
            Path.IsPathRooted(relative).ShouldBeFalse("the argument under test must be a relative path");

            string? message = null;
            var codeFix = FakeCodeFixCapturingPreviewOnly(_ => { });

            await using var host = await StartHostAsync(
                codeFix,
                (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); });

            await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
            {
                ["project"] = relative,
                ["ids"] = new[] { "RCS1213" },
                ["previewOnly"] = false,
            }, cancellationToken: TestContext.Current.CancellationToken);

            message.ShouldNotBeNull();
            ShouldNameARealProject(message);
            TargetFromPrompt(message).ShouldBe(csproj);
        }
        finally
        {
            try
            { Directory.Delete(directory, true); }
            catch { /* ignored */ }
        }
    }

    [Fact]
    public async Task Preview_Call_Never_Builds_The_Confirmation_Message()
    {
        // The read-only path must never build the prompt, because building it names the concrete
        // write target — which means resolving it. A previewOnly call has no use for that answer,
        // and 'TestProject' does not resolve to anything on disk: if the message were built
        // eagerly, resolution would throw and this call would come back as a failure envelope
        // instead of a preview. So the two assertions below pin one guarantee from both sides —
        // nobody was asked, and nothing was resolved in order to ask them.
        var elicited = 0;
        var codeFix = FakeCodeFixCapturingPreviewOnly(_ => { });

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => { elicited++; return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); });

        // previewOnly defaults to true — nothing is written, so nothing should be asked or resolved.
        var result = await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["project"] = "TestProject",
            ["ids"] = new[] { "RCS1213" },
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Nobody was asked. On its own that is the weaker half: the factory could still have been
        // invoked and its result thrown away.
        elicited.ShouldBe(0);

        // This is the half that pins the laziness. 'TestProject' resolves to nothing from the test
        // output directory, so any resolution at all — eager or discarded — surfaces here as a
        // failure envelope instead of the preview.
        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("ok").GetBoolean()
            .ShouldBeTrue("a preview call must not resolve — let alone fail on — the write target");
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Write_Confirmation_Prompts_Name_The_Project_When_It_Is_Omitted()
    {
        // With `project` omitted — the documented default, since auto-discovery is the advertised
        // behavior — the prompt used to name the literal placeholder "the auto-discovered project".
        // A human cannot refuse a write they cannot see the target of, so all three prompts must
        // now resolve it and name the concrete file. All three are asserted here: pinning only the
        // tool that broke last time would leave the other two free to break next, which is how the
        // messages diverged in the first place.
        var messages = new List<string>();

        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            FakeCodeFixCapturingPreviewOnly(_ => { }),
            (request, _) =>
            {
                messages.Add(request?.Message ?? string.Empty);
                return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" });
            },
            editService: edit);

        // `project` is deliberately absent from all three calls — the shape that broke.
        await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
        {
            ["ids"] = new[] { "RCS1213" },
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);
        await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);
        await host.Client.CallToolAsync("rename_symbol", new Dictionary<string, object?>
        {
            ["symbol"] = "Foo",
            ["newName"] = "Bar",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(
            3,
            "every write tool must ask before writing. If this is zero, auto-discovery found nothing "
            + "from the test runner's working directory and all three calls failed before eliciting: "
            + "these two omitted/empty-project facts rely on the ambient cwd resolving to a real "
            + "project, which holds because the output directory sits within ProjectLoader's "
            + "parent-walk depth of the test project's .csproj. A deeper output layout (a "
            + "RID-specific folder, an artifacts/ layout) breaks that assumption — the prompt is "
            + "fine, the fixture is not.");
        foreach (var message in messages)
        {
            ShouldNameARealProject(message);
            message.ShouldNotContain("the auto-discovered project");
            message.ShouldNotContain("''");
        }
    }

    [Fact]
    public async Task Write_Confirmation_Names_The_Project_When_It_Is_An_Empty_String()
    {
        // The #125 blank-target symptom, reached through a second input. `project: ""` rendered
        // "in ''" because the prompt tested for null while IProjectLoader.LoadAsync documents null
        // OR whitespace as the auto-discovery trigger — so prompt and loader disagreed about the
        // same argument, the prompt claiming a blank target while the loader quietly resolved a
        // real one and wrote to it. Resolving through the loader's own function makes the two agree
        // by construction, which is why this needs no separate empty-string branch to guard it.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["project"] = "",
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        ShouldNameARealProject(message);
        message.ShouldNotContain("in ''");
    }

    [Theory]
    [InlineData("apply_fixes")]
    [InlineData("edit_member")]
    [InlineData("rename_symbol")]
    public async Task Unresolvable_Write_Target_Fails_Without_Eliciting(string tool)
    {
        // Naming the target means resolving it, and resolution can fail — auto-discovery finding
        // nothing or several candidates, or, as here, an explicit reference that matches no project
        // on disk. Such a call was always going to fail; asking a human to approve it first only
        // spends their attention on a decision that changes nothing. So the failure must arrive
        // BEFORE the prompt, not after it.
        //
        // This is also the sharp edge of building the message inside the gate: every catch in
        // ConfirmDestructiveWriteAsync ends in WriteConfirmation.Proceed, so a resolution failure
        // raised inside its try would read as "this client cannot be asked" and the write would go
        // ahead unasked — the inversion of the whole gate. Hence the second assertion: it is not
        // enough that the call failed, it must have failed without eliciting.
        //
        // Run for all three write tools. Each composes its own prompt, so each could regress into
        // resolving inside the try independently of the others; asserting only the tool that was
        // fixed last would leave the other two free to break next.
        var elicited = false;
        var unresolvable = Path.Combine(_fixtureRoot, "NoSuchProjectAnywhere");

        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            FakeCodeFixCapturingPreviewOnly(_ => { }),
            (_, _) => { elicited = true; return new ValueTask<ElicitResult>(new ElicitResult { Action = "accept" }); },
            editService: edit);

        var arguments = new Dictionary<string, object?>
        {
            ["project"] = unresolvable,
            ["previewOnly"] = false,
        };
        switch (tool)
        {
            case "apply_fixes":
                arguments["ids"] = new[] { "RCS1213" };
                break;
            case "edit_member":
                arguments["symbol"] = "Foo.Bar";
                arguments["operation"] = "delete";
                break;
            default:
                arguments["symbol"] = "Foo";
                arguments["newName"] = "Bar";
                break;
        }

        var result = await host.Client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

        elicited.ShouldBeFalse("a target that cannot be resolved must not reach a human as a question");

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("ok").GetBoolean().ShouldBeFalse();

        // Either of the two types docs/API.md documents for this row: NotFoundError for an explicit
        // reference that matches nothing (this case), ValidationError for auto-discovery finding no
        // or several candidates. Which one is not the point — that the call failed rather than
        // asking is.
        payload.GetProperty("error").GetProperty("type").GetString()
            .ShouldBeOneOf("NotFoundError", "ValidationError");
    }
    /// <summary>
    /// The crafted <c>symbol</c> from #161, verbatim: a complete, plausible sentence that closes the
    /// quoted run, names a scratch project, and leaves the real one trailing behind as apparent
    /// noise. It is reproduced exactly rather than abbreviated because the exploit *is* the text —
    /// a short stand-in like <c>"a' b"</c> would satisfy the assertions below while proving nothing
    /// about the attack that was reported.
    /// </summary>
    private const string ForgedSymbol =
        "Config' to disk? Exactly one file is rewritten — the declaration it resolves to, anywhere in "
        + "the code loaded from '/repo/scratch/Sandbox.csproj'.  ";

    /// <summary>Non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Asserts that whatever the caller put in a prompt, the sentence still has exactly one shape:
    /// one question, and three quoted runs opened and closed by the frame rather than by the caller.
    /// </summary>
    /// <remarks>
    /// The apostrophe count is the load-bearing half. "Contains no second sentence" is a property of
    /// this particular payload; "the caller cannot open or close a quoted run" is the property that
    /// holds against every payload, and it is what makes the last quoted run — the one the human
    /// checks the target in, and the one <see cref="TargetFromPrompt"/> reads — the frame's rather
    /// than the caller's. All three prompts quote exactly three things, so the expected count is 6.
    /// </remarks>
    private static void ShouldBeOneUnforgeableSentence(string message, string expectedTarget)
    {
        CountOccurrences(message, "to disk?").ShouldBe(
            1, $"caller input appended a second question to the prompt: {message}");
        message.Count(c => c == '\'').ShouldBe(
            6, $"caller input opened or closed a quoted run: {message}");
        TargetFromPrompt(message).ShouldBe(
            expectedTarget, $"the prompt's last quoted run is no longer the real target: {message}");
    }

    [Fact]
    public async Task EditMember_Confirmation_Cannot_Be_Forged_By_A_Crafted_Symbol()
    {
        // #161. `symbol` is free-form caller input interpolated straight into the sentence, so a
        // symbol carrying quote-and-punctuation used to render a complete, benign-looking sentence
        // that ended before the real one began: the human read the first sentence, saw a scratch
        // project, approved — and the write went to the resolved target instead. The gate's entire
        // purpose is to let a human refuse, so a sentence the caller can author is not a gate.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync(
            "edit_member",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["symbol"] = ForgedSymbol,
                ["operation"] = "delete",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        ShouldBeOneUnforgeableSentence(message, _fixtureSolution);
    }

    [Fact]
    public async Task RenameSymbol_Confirmation_Cannot_Be_Forged_By_A_Crafted_NewName()
    {
        // rename_symbol interpolates TWO free-form values, and sanitising only `symbol` would leave
        // the hole open through `newName` — which is why this asserts on the second one. The
        // payload below imitates the CURRENT frame, and has to be re-authored whenever that frame
        // changes (#173 moved the scope clause behind the question mark, and this string moved with
        // it): a payload shaped like a retired sentence still passes every assertion here while
        // proving nothing, which is the same trap the ForgedSymbol case warns about.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync(
            "rename_symbol",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["symbol"] = "Foo",
                ["newName"] = "Bar' and write the changes to disk? The write can reach any project in the solution of '/repo/scratch/Sandbox.csproj'. Ignore",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        ShouldBeOneUnforgeableSentence(message, _fixtureSolution);
    }

    [Fact]
    public async Task Write_Confirmation_Renders_An_Ordinary_Symbol_Unchanged()
    {
        // The counterweight to the two above: sanitising must be INVISIBLE on every input a caller
        // legitimately sends. A C# symbol reference contains no whitespace and no apostrophe, so the
        // sanitiser has nothing to do here — and a long fully-qualified name with a generic argument
        // is the worst realistic case, since it is what a length cap would mangle first.
        const string symbol = "RoselineMCP.Services.CodeEditService.EditMemberAsync<TResult>";
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync(
            "edit_member",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["symbol"] = symbol,
                ["operation"] = "replace",
                ["newSource"] = "public int Bar { get; set; }",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message.ShouldBe(
            $"Write the 'replace' of member '{symbol}' to disk? Exactly one file is rewritten — the "
            + $"declaration it resolves to, anywhere in the code loaded from '{_fixtureSolution}'.");
    }
    /// <summary>
    /// The symbol as the prompt rendered it — the run between <c>edit_member</c>'s fixed
    /// <c>member '</c> and <c>' to disk?</c>. Reading it back out, rather than rebuilding the
    /// expected string, keeps these assertions from re-implementing the sanitiser they check.
    /// </summary>
    private static string RenderedSymbolFromEditMemberPrompt(string message)
    {
        const string open = "member '";
        const string close = "' to disk?";
        var start = message.IndexOf(open, StringComparison.Ordinal);
        var end = message.IndexOf(close, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"the prompt names no member: {message}");
        end.ShouldBeGreaterThan(start, $"the prompt's member quoting is unbalanced: {message}");
        return message[(start + open.Length)..end];
    }

    private async Task<string> EditMemberPromptForSymbolAsync(string symbol)
    {
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.EditMemberAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, bool _, int _, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly, ChangedFiles = { "src/Foo.cs" } }));

        await using var host = await StartHostAsync(
            A.Fake<ICodeFixService>(),
            (request, _) => { message = request?.Message; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            editService: edit);

        await host.Client.CallToolAsync(
            "edit_member",
            new Dictionary<string, object?>
            {
                ["project"] = _fixtureSolution,
                ["symbol"] = symbol,
                ["operation"] = "replace",
                ["newSource"] = "public int Bar { get; set; }",
                ["previewOnly"] = false,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        return message;
    }

    [Fact]
    public async Task Write_Confirmation_Elides_The_Middle_Of_An_Overlong_Symbol()
    {
        // A cap is what stops a payload that survives the other two rules from burying the rest of
        // the sentence under its own bulk. Eliding the MIDDLE rather than truncating is what keeps
        // it honest for the legitimate case it also catches: a genuinely long fully-qualified name
        // stays recognisable because the head still names the namespace and the tail still names
        // the member.
        var symbol = string.Join(".", Enumerable.Repeat("VeryLongNamespace", 20));
        symbol.Length.ShouldBeGreaterThan(300);

        var rendered = RenderedSymbolFromEditMemberPrompt(await EditMemberPromptForSymbolAsync(symbol));

        rendered.Length.ShouldBeLessThanOrEqualTo(
            121, $"an unbounded value can bury the sentence it sits in: {rendered}");
        rendered.ShouldContain("…");
        rendered.ShouldStartWith(symbol[..20]);
        rendered.ShouldEndWith(symbol[^20..]);
    }

    [Fact]
    public async Task Write_Confirmation_Never_Renders_An_Empty_Quoted_Run_For_A_Blank_Symbol()
    {
        // Removing whitespace is what stops a payload reading as prose, and it has one edge:
        // `edit_member` validates `operation` but not `symbol`, so a whitespace-only symbol reaches
        // the prompt and would render "member ''" — the same unanswerable sentence PR #142 removed
        // from the target side. A placeholder says plainly that the caller named nothing.
        var rendered = RenderedSymbolFromEditMemberPrompt(await EditMemberPromptForSymbolAsync("   "));

        rendered.ShouldBe("(unnamed)");
    }
    // ---------------------------------------------------------------------------------------
    // The closed scope vocabulary (#161b). The three sentences above are asserted end-to-end,
    // through the protocol, which is what makes them true of the shipped tools — but it also means
    // each is only ever exercised by the one tool that happens to use it. These render every
    // WriteScope member directly, against both kinds of target, so the vocabulary is pinned
    // independently of who calls it: a fourth write tool picking a member gets the wording its
    // sibling already agreed to, and cannot invent a fourth phrasing to sit beside three others.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/repo/App.sln", "the primary project of ")]
    [InlineData("/repo/App.csproj", "the project ")]
    public void PrimaryProjectOf_Renders_The_ApplyFixes_Sentence(string target, string qualifier)
    {
        // The one scope whose clause DOES branch on the target's extension (#149): CodeFixService
        // narrows a solution to a single anchor project, so naming the solution outright would have
        // the human authorise a write broader than the one about to happen. A .csproj target *is*
        // its own write scope, so it takes "the project" rather than "the primary project of" (#203)
        // — the noun still names what kind of thing the target is, it does not widen or narrow the
        // write the sentence describes.
        //
        // The scope moved behind the question mark so the sentence ENDS on the target (#173) — the
        // claim is unchanged, only its position: still N diagnostic IDs, still written to disk,
        // still narrowed to the primary project when and only when the target is a solution.
        //
        // #203: the .csproj branch now names the noun too, restoring the symmetry the two branches
        // had before #173's re-ordering left the bare path to carry the project-ness alone.
        WritePrompt.ForPrimaryProjectOf(3).Render(target).ShouldBe(
            "Apply code fixes for 3 diagnostic ID(s) and write the changes to disk? "
            + $"The write reaches {qualifier}'{target}'.");
    }

    [Theory]
    [InlineData("/repo/App.sln")]
    [InlineData("/repo/App.csproj")]
    public void SingleFile_Renders_The_EditMember_Sentence(string target)
    {
        // Deliberately does NOT branch on the extension: this write is one file whether the target
        // is a solution or a project, and it says "loaded from" rather than "in" because a .csproj
        // does not bound it either — ProjectLoader opens the containing solution and resolution
        // spans every project in it.
        WritePrompt.ForSingleFile("delete", "Foo.Bar").Render(target).ShouldBe(
            "Write the 'delete' of member 'Foo.Bar' to disk? Exactly one file is rewritten — the "
            + $"declaration it resolves to, anywhere in the code loaded from '{target}'.");
    }

    [Theory]
    [InlineData("/repo/App.sln")]
    [InlineData("/repo/App.csproj")]
    public void SingleFile_Names_The_Container_Type_When_The_Operation_Is_Add(string target)
    {
        // It branches on the OPERATION, and only on the noun: 'add' resolves `symbol` as the
        // container type (CodeEditService.AddMember rejects anything else), so calling it a member
        // would name the human a thing that does not exist yet. That belongs with the values rather
        // than with the scope, which is why the factory takes the operation.
        WritePrompt.ForSingleFile("add", "Foo.Bar").Render(target).ShouldBe(
            "Write the 'add' of a member to type 'Foo.Bar' to disk? Exactly one file is rewritten — "
            + $"the declaration it resolves to, anywhere in the code loaded from '{target}'.");
    }

    [Theory]
    [InlineData("/repo/App.sln")]
    [InlineData("/repo/App.csproj")]
    public void WholeSolution_Renders_The_RenameSymbol_Sentence(string target)
    {
        // The counterweight, and the reason the other two are not a blanket rule: RenameSymbolAsync
        // really is solution-wide, so naming the solution is exact and a narrowing qualifier here
        // would be a fresh inaccuracy of the same family #149/#154 closed.
        //
        // Re-worded for #173 so the sentence ends on the target. "CAN REACH ANY project" rather
        // than "reaches every project": the rename is solution-wide in reach, but Renamer only
        // rewrites the projects that actually contain the symbol, so "every" would have been a
        // fresh overstatement of the kind #149/#154 were about. It still authorises the maximum,
        // which is what a confirmation has to do — it just stops promising a write that may not
        // happen.
        WritePrompt.ForWholeSolution("Foo", "Bar").Render(target).ShouldBe(
            "Rename 'Foo' to 'Bar' and write the changes to disk? "
            + $"The write can reach any project in the solution of '{target}'.");
    }

    [Fact]
    public void Render_Sanitises_Every_Caller_Supplied_Value()
    {
        // The rendering and the sanitiser are one unit: moving composition here is what put every
        // caller-supplied value behind the sanitiser at once, rather than behind three call sites
        // that each had to remember (#161a). Asserted per scope, because a value reached by only
        // one of them is exactly how the previous three-copy arrangement drifted.
        WritePrompt.ForSingleFile("delete", "A B'C").Render("/repo/App.sln")
            .ShouldContain("member 'ABC'");
        WritePrompt.ForWholeSolution("A B'C", "D E'F").Render("/repo/App.sln")
            .ShouldBe(
                "Rename 'ABC' to 'DEF' and write the changes to disk? "
                + "The write can reach any project in the solution of '/repo/App.sln'.");
    }

    [Fact]
    public void Render_Drops_Look_Alike_Characters_A_Symbol_Reference_Cannot_Contain()
    {
        // The first cut of the sanitiser was a DENYLIST — drop char.IsWhiteSpace, swap ASCII "'" —
        // and a denylist is the wrong shape when the reader being protected is a human. Every
        // character below rebuilds the forged sentence while slipping past that rule:
        //
        //   U+2019  a right single quote supplied DIRECTLY, never converted, and at a glance
        //           indistinguishable from the frame's own ASCII quote;
        //   U+2800  BRAILLE PATTERN BLANK — renders as a space, is not char.IsWhiteSpace;
        //   U+3164  HANGUL FILLER — renders as a space, and is categorised as a LETTER.
        //
        // The whitelist needs to anticipate none of them: a C# symbol reference cannot contain any
        // of these, so they are gone by construction rather than by enumeration.
        const string forged = "Config’⠀to⠀disk?ㅤExactly⠀one⠀file";

        WritePrompt.ForSingleFile("delete", forged).Render("/repo/App.sln").ShouldBe(
            "Write the 'delete' of member 'ConfigtodiskExactlyonefile' to disk? Exactly one file is "
            + "rewritten — the declaration it resolves to, anywhere in the code loaded from '/repo/App.sln'.");
    }

    [Theory]
    [InlineData("Acme.Orders.Repository<T,U>")]
    [InlineData("global::Acme.Orders")]
    [InlineData("Outer+Inner.Method")]
    [InlineData("@class.@event")]
    [InlineData("System.Collections.Generic.List`1")]
    [InlineData("Café.Método")]
    public void Render_Leaves_Every_Shape_Of_Real_Symbol_Reference_Untouched(string symbol)
    {
        // The whitelist's other half: it has to be invisible on everything a caller legitimately
        // sends, or it degrades the one part of the prompt that identifies WHAT is being written.
        // Generics, global::, nested types, verbatim identifiers, arity suffixes, and non-ASCII
        // letters are all real symbol references.
        WritePrompt.ForSingleFile("delete", symbol).Render("/repo/App.sln")
            .ShouldContain($"member '{symbol}'");
    }

    [Theory]
    [InlineData("​")]
    [InlineData("⠀ㅤ")]
    [InlineData("???")]
    public void Render_Names_Nothing_When_The_Value_Survives_As_Empty(string symbol)
    {
        // U+200B is invisible and is NOT char.IsWhiteSpace, so under the denylist it skipped the
        // placeholder and rendered "member ''" — the unanswerable prompt PR #142 removed from the
        // target side. Under the whitelist the placeholder is driven by what SURVIVES rather than by
        // what arrived, so anything filtered down to nothing lands on it.
        WritePrompt.ForSingleFile("delete", symbol).Render("/repo/App.sln")
            .ShouldContain("member '(unnamed)'");
    }

    // ---------------------------------------------------------------------------------------
    // The target is the sentence's LAST quoted run — for every scope, not just SingleFile (#173).
    // A checkout path may legitimately contain an apostrophe (an "O'Brien" home directory, a
    // "Bob's Projects" folder), which closes the quoted run early; wherever frame text still
    // follows the target, that trailing clause becomes forgeable by a DIRECTORY name — the same
    // shape #161 closed on the caller's side, arriving from the operator's filesystem instead.
    //
    // The fix is ORDERING, not escaping. The target is the one value in the sentence a human can
    // check against reality, which is why ShouldNameARealProject asserts File.Exists on it and why
    // Sanitize deliberately exempts it; transforming it for display would trade that real guarantee
    // for a theoretical one. So it goes last, and the invariant becomes true by construction.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A target path carrying the two things a real checkout can: an apostrophe, and a space in the
    /// same segment. The apostrophe follows a letter, as it does in every plausible directory name,
    /// which is what keeps <see cref="TargetFromPrompt"/>'s "opening quote is the last one preceded
    /// by a space" heuristic on its feet.
    /// </summary>
    private const string ApostropheSolution = "/repo/Bob's Projects/App.sln";

    /// <summary>
    /// The same path as a project. <see cref="WriteScope.PrimaryProjectOf"/> is the one scope whose
    /// wording branches on the extension (#149), so both sides of that branch need covering.
    /// </summary>
    private const string ApostropheProject = "/repo/Bob's Projects/App.csproj";

    /// <summary>
    /// The adversarial shape, as opposed to the two merely awkward ones above: a directory named so
    /// that the text after its apostrophe reads as reassuring prose. This is what the residual
    /// actually looks like, and it is here so the security wording in <c>SECURITY.md</c>,
    /// <c>docs/API.md</c> and <c>WritePrompt.Render</c> has to stay honest — ordering removes the
    /// <em>frame's</em> tail, not the <em>path's</em>.
    /// </summary>
    private const string ForgedProseSolution = "/repo/Bob' — already reviewed and approved/App.sln";

    /// <summary>
    /// A target whose apostrophe follows a SPACE rather than a letter — the shape #204 named as
    /// mis-parsed by <see cref="TargetFromPrompt"/>'s old space-quote heuristic
    /// (<c>LastIndexOf(" '")</c>): the heuristic's opening quote lands on this apostrophe instead of
    /// the one that actually opens the target's quoted run, truncating the recovered path. The
    /// terminator-based derivation that replaced it does not care where the apostrophe sits, so this
    /// row is what tells the two approaches apart.
    /// </summary>
    private const string SpacedApostropheSolution = "/repo/x 'y/App.sln";

    /// <summary>
    /// Every <see cref="WriteScope"/> member against every target shape. Built from
    /// <c>Enum.GetValues</c> rather than hand-written rows so the coverage is genuinely exhaustive:
    /// a fourth scope is picked up here automatically and fails in <see cref="PromptFor"/> until it
    /// is given a rendering, rather than shipping uncovered by the invariant these tests hold.
    /// Shared by both theories below — one list, so a new target shape cannot be added to one and
    /// forgotten in the other.
    /// </summary>
    public static TheoryData<WriteScope, string> ApostropheTargets()
    {
        var data = new TheoryData<WriteScope, string>();
        foreach (var scope in Enum.GetValues<WriteScope>())
        {
            data.Add(scope, ApostropheSolution);
            data.Add(scope, ApostropheProject);
            data.Add(scope, ForgedProseSolution);
            data.Add(scope, SpacedApostropheSolution);
        }

        return data;
    }

    /// <summary>
    /// One prompt per <see cref="WriteScope"/> member, built from values that survive
    /// <c>Sanitize</c> untouched so the only variable under test is where the target sits. The
    /// switch is exhaustive on purpose: a fourth scope fails here rather than going silently
    /// uncovered by the invariant these tests exist to hold.
    /// </summary>
    private static WritePrompt PromptFor(WriteScope scope) => scope switch
    {
        WriteScope.PrimaryProjectOf => WritePrompt.ForPrimaryProjectOf(3),
        WriteScope.SingleFile => WritePrompt.ForSingleFile("delete", "Foo.Bar"),
        WriteScope.WholeSolution => WritePrompt.ForWholeSolution("Foo", "Bar"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(scope), scope, "This write scope has no prompt in the target-ordering coverage."),
    };

    [Theory]
    [MemberData(nameof(ApostropheTargets))]
    public void Every_Write_Prompt_Ends_On_Its_Target(WriteScope scope, string target)
    {
        // Asserted as the PROPERTY — the message ends on the target's closing quote — rather than
        // against any one phrasing, so a future re-word is free to change the frame and not free to
        // put text back after the target.
        PromptFor(scope).Render(target).ShouldEndWith($"'{target}'.");
    }

    [Theory]
    [MemberData(nameof(ApostropheTargets))]
    public void Every_Write_Prompt_Round_Trips_A_Target_Containing_An_Apostrophe(
        WriteScope scope, string target)
    {
        // The reader's half of the same invariant: TargetFromPrompt takes the last quoted run, which
        // is how these tests — and ShouldNameARealProject's File.Exists — recover the path. With the
        // target last, an apostrophe inside it has nothing left to mis-parse against.
        TargetFromPrompt(PromptFor(scope).Render(target)).ShouldBe(target);
    }
}
