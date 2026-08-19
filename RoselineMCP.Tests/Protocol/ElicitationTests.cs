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
/// </summary>
[Collection(McpProtocolCollection.Name)]
public class ElicitationTests
{
    private static Task<McpProtocolTestHost> StartHostAsync(
        ICodeFixService codeFixService,
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> elicitationHandler,
        Action<RoselineMcpOptions>? configureOptions = null)
        => McpProtocolTestHost.StartAsync(
            services =>
            {
                services.AddSingleton(codeFixService);
                services.AddSingleton(A.Fake<ISolutionAnalyzerService>());
                services.AddSingleton(A.Fake<ICodeNavigationService>());
                services.AddSingleton(A.Fake<ICodeEditService>());
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
            ["project"] = "TestProject",
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
            ["project"] = "TestProject",
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
                ["project"] = "TestProject",
                ["ids"] = new[] { "RCS1213" },
                ["previewOnly"] = false,
            });

        var call = CallApplyFixesAsync();

        // The bound is the whole point: wait — with an explicit ceiling, so a regression FAILS
        // this suite instead of wedging CI — for the server to give up on the unanswered prompt
        // and fall through to the fix service in preview mode. Unbounded, that never happens.
        var observed = await Task.WhenAny(downgraded.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        observed.ShouldBeSameAs(
            downgraded.Task,
            "the write confirmation was never bounded — the server never stopped waiting for an answer");

        // Silence is not consent: the write was downgraded to a preview, nothing reached disk.
        (await downgraded.Task).ShouldBeTrue();

        // Only now release the client's handler. This in-process harness dispatches
        // server-initiated requests on the client's single read loop, so a handler that never
        // returns would also stop the client from ever reading the tool response — an artifact of
        // the harness, not of the server, which has already moved on. The late answer changes
        // nothing precisely because the elicitation it belongs to was abandoned when the deadline
        // fired.
        neverAnswers.TrySetResult(new ElicitResult { Action = "decline" });

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
        // SECURITY.md and the CHANGELOG all promise. This reproduces that ordering three orders
        // of magnitude faster: analysis budget 300 ms, confirmation budget 1200 ms.
        bool? captured = null;

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
                return Task.FromResult(new ApplyFixesResponse { PreviewOnly = previewOnly });
            });

        var neverAnswers = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var host = await StartHostAsync(
            codeFix,
            (_, _) => new ValueTask<ElicitResult>(neverAnswers.Task),
            options =>
            {
                options.DefaultTimeout = 300;
                options.ConfirmDestructiveWritesTimeout = 1200;
            });

        // This harness dispatches server-initiated requests on the client's single read loop, so
        // the pending handler has to be released before the client can read the tool response the
        // server already wrote. Well after the confirmation deadline, so the server has long since
        // given up; the late answer is ignored.
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            neverAnswers.TrySetResult(new ElicitResult { Action = "decline" });
        });

        async Task<CallToolResult> CallApplyFixesAsync() =>
            await host.Client.CallToolAsync("apply_fixes", new Dictionary<string, object?>
            {
                ["project"] = "TestProject",
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

        await using var host = await McpProtocolTestHost.StartAsync(
            services =>
            {
                services.AddSingleton(A.Fake<ISolutionAnalyzerService>());
                services.AddSingleton(A.Fake<ICodeFixService>());
                services.AddSingleton(A.Fake<ICodeNavigationService>());
                services.AddSingleton(edit);
                services.AddSingleton<IDiffService, DiffService>();
                services.AddSingleton<IPatchService, PatchService>();
            },
            (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }));

        await host.Client.CallToolAsync("rename_symbol", new Dictionary<string, object?>
        {
            ["project"] = "Demo",
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
        // second tool, mirroring the decline-path test above.
        bool? captured = null;
        var elicited = false;
        var edit = A.Fake<ICodeEditService>();
        A.CallTo(() => edit.RenameSymbolAsync(
                A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Invokes((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                captured = previewOnly)
            .ReturnsLazily((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                Task.FromResult(new RenameSymbolResponse { PreviewOnly = previewOnly }));

        await using var host = await McpProtocolTestHost.StartAsync(
            services =>
            {
                services.AddSingleton(A.Fake<ISolutionAnalyzerService>());
                services.AddSingleton(A.Fake<ICodeFixService>());
                services.AddSingleton(A.Fake<ICodeNavigationService>());
                services.AddSingleton(edit);
                services.AddSingleton<IDiffService, DiffService>();
                services.AddSingleton<IPatchService, PatchService>();
            },
            (_, _) => { elicited = true; return new ValueTask<ElicitResult>(new ElicitResult { Action = "decline" }); },
            options => options.ConfirmDestructiveWrites = false);

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
}
