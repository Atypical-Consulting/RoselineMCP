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
        try { Directory.Delete(_fixtureRoot, true); } catch { /* ignored */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The write target named by a confirmation prompt — the last quoted segment, which is where
    /// all three messages put it ("… anywhere in the code loaded from '&lt;target&gt;'."). Reading
    /// it out of the message, rather than re-deriving the expected path, is what keeps these
    /// assertions from re-implementing the resolution they are supposed to be checking.
    /// </summary>
    /// <remarks>
    /// Two constraints pull against each other here, which is why this is hand-rolled rather than a
    /// quoted-run regex. The messages quote other things first ("… the 'delete' of member 'Foo.Bar'
    /// to disk? … loaded from '&lt;target&gt;'."), so the parser cannot simply
    /// take the widest quoted span; and a
    /// resolved path may itself contain an apostrophe — <c>C:\Users\O'Brien\src</c>,
    /// <c>~/Bob's Projects</c> — so it cannot take the narrowest one either. The opening quote is
    /// therefore identified as the last one that follows a space (an apostrophe inside a path
    /// follows a letter), and the closing quote as the last in the message, since every message's
    /// tail after the target holds none.
    /// </remarks>
    private static string TargetFromPrompt(string message)
    {
        var open = message.LastIndexOf(" '", StringComparison.Ordinal);
        var close = message.LastIndexOf('\'');
        open.ShouldBeGreaterThanOrEqualTo(0, $"the prompt names no target at all: {message}");
        close.ShouldBeGreaterThan(open + 1, $"the prompt's target quoting is unbalanced: {message}");
        return message[(open + 2)..close];
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
                A<string>._, A<List<string>>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, List<string> _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                capture(previewOnly))
            .ReturnsLazily((string _, List<string> _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly }));
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
        });

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
        });

        // Accepted → the write proceeds; the service was invoked with previewOnly = false.
        captured.ShouldBe(false);
    }

    [Fact]
    public async Task ApplyFixes_With_PreviewOnly_False_Is_Downgraded_To_Preview_When_Confirmation_Times_Out()
    {
        bool? captured = null;
        var downgraded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codeFix = FakeCodeFixCapturingPreviewOnly(p =>
        {
            captured = p;
            downgraded.TrySetResult(p);
        });

        // A client that advertises elicitation support, accepts the request, and then never
        // answers — the unattended-host case. Before the round-trip was bounded this wedged the
        // tool call forever: RoselineMCP:DefaultTimeout does not apply to the confirmation by
        // construction, so nothing else could ever end the wait.
        var neverAnswers = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => new ValueTask<ElicitResult>(neverAnswers.Task),
            options => options.ConfirmDestructiveWritesTimeout = 200);

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

        // Release the client's handler as soon as the downgrade is observed — and unconditionally
        // after a ceiling, even if it never is. The ordering matters twice over. The SDK's own
        // McpClient dispatches server-initiated requests on its single read loop, so a handler
        // that never returns also stops the client reading the tool response the server has
        // already written; and if the assertion below were the thing gating the release, a
        // regression would leave the handler parked forever, so `await using host` could not drain
        // the read loop and the whole (DisableParallelization) protocol collection would hang —
        // CI wedging instead of reporting the very regression this test exists to catch.
        var release = Task.Run(async () =>
        {
            await Task.WhenAny(downgraded.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            neverAnswers.TrySetResult(new ElicitResult { Action = "decline" });
        });

        // The bound is the whole point: wait — with an explicit ceiling, so a regression FAILS
        // this suite instead of wedging CI — for the server to give up on the unanswered prompt
        // and fall through to the fix service in preview mode. Unbounded, that never happens.
        var observed = await Task.WhenAny(downgraded.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        observed.ShouldBeSameAs(
            downgraded.Task,
            "the write confirmation was never bounded — the server never stopped waiting for an answer");

        // Silence is not consent: the write was downgraded to a preview, nothing reached disk.
        (await downgraded.Task).ShouldBeTrue();

        // The late answer changes nothing precisely because the elicitation it belongs to was
        // abandoned when the deadline fired.
        await release;

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(15)));
        finished.ShouldBeSameAs(call, "the tool call did not return after the confirmation timed out");

        var result = await call;
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
        var serviceRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var codeFix = A.Fake<ICodeFixService>();
        A.CallTo(() => codeFix.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, List<string> _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken ct) =>
            {
                // The real service checks the token before doing anything, so a fake that ignores
                // it cannot see an analysis budget that already expired — which is precisely the
                // failure being guarded against here.
                ct.ThrowIfCancellationRequested();
                captured = previewOnly;
                serviceRan.TrySetResult(previewOnly);
                return Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly });
            });

        var neverAnswers = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => new ValueTask<ElicitResult>(neverAnswers.Task),
            options =>
            {
                options.DefaultTimeout = 1000;
                options.ConfirmDestructiveWritesTimeout = 2500;
            });

        // The SDK's McpClient dispatches server-initiated requests on its single read loop, so the
        // pending handler has to be released before the client can read the tool response the
        // server already wrote. Keyed off the service actually running — which only happens once
        // the gate has given up on the prompt — rather than a fixed sleep: a hardcoded delay is
        // both a flat cost paid by every other test in this DisableParallelization collection and
        // a magic number silently coupled to ConfirmDestructiveWritesTimeout above. The ceiling
        // still releases the handler if the service never runs, so a regression fails this test
        // instead of wedging the collection.
        var release = Task.Run(async () =>
        {
            await Task.WhenAny(serviceRan.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            neverAnswers.TrySetResult(new ElicitResult { Action = "decline" });
        });

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
        });

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
                A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly }));

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
        });

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
                A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly }));

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
        });

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
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly }));

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
        });

        captured.ShouldBe(true);

        var payload = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        payload.GetProperty("data").GetProperty("previewOnly").GetBoolean().ShouldBeTrue();
        payload.GetProperty("data").GetProperty("notes").EnumerateArray()
            .Select(n => n.GetString()).ShouldContain(s => s!.Contains("declined"));
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
        });

        message.ShouldNotBeNull();
        message.ShouldContain(_fixtureProject);
    }

    [Fact]
    public async Task ApplyFixes_Confirmation_Names_The_Primary_Project_When_The_Target_Is_A_Solution()
    {
        // ApplyFixes is a PROJECT-scoped tool whose resolved write target may be a SOLUTION:
        // CodeFixService narrows it to one project (ProjectLoader.SelectPrimaryProject) and fixes
        // only that project's documents. A prompt naming the .sln therefore describes a broader
        // scope than the write actually has — the human reading "…to '/repo/Acme.sln'" cannot tell
        // that two of their three projects will be untouched. The sentence has to say so.
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
        // substring: when the resolved target is already the project that gets written, today's
        // wording is exact and the scope qualifier would be a fresh inaccuracy. A test that only
        // checked for the .csproj path would still pass if the qualifier fired on both branches.
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
        message.ShouldBe($"Apply code fixes for 1 diagnostic ID(s) to '{_fixtureProject}' and write the changes to disk?");
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
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly }));

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
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly }));

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
    public async Task RenameSymbol_Confirmation_Names_The_Solution_Without_A_Scope_Qualifier()
    {
        // The counterweight to the two above, and the reason they are not a blanket rule.
        // RenameSymbolAsync really is solution-wide — Renamer.RenameSymbolAsync, then every changed
        // project, then every changed file — so naming the solution is exact and a "single file"
        // qualifier here would be a fresh inaccuracy of the same family this issue is closing.
        // Asserted byte-for-byte: a substring check would not notice the qualifier leaking in.
        string? message = null;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly }));

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
        message.ShouldBe($"Rename 'Foo' to 'Bar' across the solution of '{_fixtureSolution}' and write the changes to disk?");
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
                A<string>._, A<List<string>>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string project, List<string> _, bool _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                projectSeenByService = project)
            .ReturnsLazily((string _, List<string> _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly }));

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
        });

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
            });

            message.ShouldNotBeNull();
            ShouldNameARealProject(message);
            TargetFromPrompt(message).ShouldBe(csproj);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* ignored */ }
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
        });

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
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly }));
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly }));

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
        });
        await host.Client.CallToolAsync("edit_member", new Dictionary<string, object?>
        {
            ["symbol"] = "Foo.Bar",
            ["operation"] = "delete",
            ["previewOnly"] = false,
        });
        await host.Client.CallToolAsync("rename_symbol", new Dictionary<string, object?>
        {
            ["symbol"] = "Foo",
            ["newName"] = "Bar",
            ["previewOnly"] = false,
        });

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
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly }));

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
        });

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
                A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, string _, bool previewOnly, CancellationToken _) =>
                Task.FromResult(new EditMemberResponse { PreviewOnly = previewOnly }));
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly }));

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

        var result = await host.Client.CallToolAsync(tool, arguments);

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
}
