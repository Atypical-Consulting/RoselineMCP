using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="GuardService"/> — the compile guard's engine, against real Roslyn
/// compilations and real files on disk.
/// </summary>
/// <remarks>
/// The tests write actual files because the service reads them back: its baseline is a Roslyn
/// <c>Solution</c> snapshot that it edits <em>forward</em> from disk, never a reload. That
/// distinction is the subject of
/// <see cref="Never_Blames_The_Agent_For_Errors_That_Were_Already_There"/>, which is a regression
/// guard for a measured defect and not a hypothetical.
/// </remarks>
public class GuardServiceTests : IDisposable
{
    private readonly string _root;

    public GuardServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RoselineGuard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, true); }
        catch { /* ignored */ }

        GC.SuppressFinalize(this);
    }

    private const string GreenSource = "public class Thing { public int Value() => 1; }";
    private const string BrokenSource = "public class Thing { public int Value() => nope; }";

    /// <summary>
    /// Ceiling for <c>Task.WhenAny(counting.Entered, Task.Delay(EnteredWaitTimeout))</c> below.
    /// Its only job is "don't hang the suite forever if the fake genuinely never reaches
    /// <c>VerifyAsync</c>" — it is not a correctness assertion, so it is deliberately generous
    /// (90s, not 30s) to stay clear of CI-runner scheduling jitter observed under load (#219).
    /// </summary>
    private static readonly TimeSpan EnteredWaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Waits for <paramref name="counting"/>'s held call to reach <c>VerifyAsync</c>, bounded by
    /// <see cref="EnteredWaitTimeout"/>, and asserts it did. Shared by both tests that hold a call
    /// open and need a second one to race it — the wait and its diagnostic message are identical at
    /// both sites; only what to call the waited-for call in the failure message differs. Delegates
    /// to <see cref="AsyncWaitHelpers.WaitForSignal"/>, the mechanism extracted for reuse by
    /// <c>ElicitationTests</c>' own ceiling-vs-completion race (#224) rather than duplicated a
    /// second time.
    /// </summary>
    private static Task WaitForEntered(CountingVerificationService counting, string label) =>
        AsyncWaitHelpers.WaitForSignal(counting.Entered, EnteredWaitTimeout, label);

    /// <summary>
    /// Builds a one-project solution whose document paths point at real files on disk, writes those
    /// files, and returns a loader that hands the solution out for any file inside it.
    /// </summary>
    private (IProjectLoader Loader, AdhocWorkspace Workspace, string SourcePath) CreateSolution(string source)
    {
        var baseDirectory = Path.Combine(_root, "sln");
        Directory.CreateDirectory(Path.Combine(baseDirectory, "Core"));

        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
            [("Core", [("Thing.cs", source)])],
            baseDirectory: baseDirectory,
            solutionFileName: "Chain.sln");

        // The service reads the edited file back off disk, so the document's path must really exist.
        var sourcePath = anchor.Documents.Single(d => d.Name == "Thing.cs").FilePath!;
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, source);

        // ...and so must the .csproj: AdhocProjectBuilder only sets FilePath as metadata, while
        // ProjectLoader.ResolveProjectForFile looks for a real file on disk. Without this the guard
        // finds no owning project and every assertion below passes vacuously.
        File.WriteAllText(anchor.FilePath!, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var loader = A.Fake<IProjectLoader>();
        A.CallTo(() => loader.LoadForFileAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<LoadedProject?>(
                new LoadedProject(workspace, anchor.Solution, anchor, ownsWorkspace: false)));

        return (loader, workspace, sourcePath);
    }

    /// <summary>
    /// A two-project solution whose files really exist, plus a loader that hands the same solution
    /// out for a file in either project — which is what a real <c>.sln</c> load does.
    /// </summary>
    private (IProjectLoader Loader, AdhocWorkspace Workspace, (string Core, string Side) Paths) CreateTwoProjectSolution()
    {
        var baseDirectory = Path.Combine(_root, "multi");

        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
            [
                ("Core", [("Thing.cs", GreenSource)]),
                ("Side", [("Side.cs", "public class Side { public int Other() => 1; }")]),
            ],
            baseDirectory: baseDirectory,
            solutionFileName: "Multi.sln");

        var paths = (Core: string.Empty, Side: string.Empty);

        foreach (var project in anchor.Solution.Projects)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(project.FilePath!)!);
            File.WriteAllText(project.FilePath!, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            foreach (var document in project.Documents)
            {
                File.WriteAllText(document.FilePath!, document.GetTextAsync().Result.ToString());

                if (project.Name == "Core")
                {
                    paths.Core = document.FilePath!;
                }
                else
                {
                    paths.Side = document.FilePath!;
                }
            }
        }

        var loader = A.Fake<IProjectLoader>();
        A.CallTo(() => loader.LoadForFileAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<LoadedProject?>(
                new LoadedProject(workspace, anchor.Solution, anchor, ownsWorkspace: false)));

        return (loader, workspace, paths);
    }

    private static GuardService CreateService(IProjectLoader loader, IVerificationService? verification = null) =>
        new(
            loader,
            verification ?? new VerificationService(
                A.Fake<ILogger<VerificationService>>(), DiagnosticComputationService.CompilerOnly),
            A.Fake<ILogger<GuardService>>());

#pragma warning disable xUnit1051 // TestContext.Current not needed here

    [Fact]
    public async Task The_First_Sighting_Of_A_Solution_Records_A_Baseline_And_Says_Nothing()
    {
        var (loader, workspace, sourcePath) = CreateSolution(GreenSource);
        using var _ = workspace;
        using var service = CreateService(loader);

        var report = await service.VerifyFileAsync(sourcePath);

        // Nothing to compare against yet — the guard cannot know what this edit changed.
        report.Silent.ShouldBeTrue();
        report.Text.ShouldBeNull();
    }

    [Fact]
    public async Task An_Edit_That_Breaks_The_Build_Is_Reported_As_Introduced()
    {
        var (loader, workspace, sourcePath) = CreateSolution(GreenSource);
        using var _ = workspace;
        using var service = CreateService(loader);

        await service.VerifyFileAsync(sourcePath);          // baseline
        File.WriteAllText(sourcePath, BrokenSource);        // the agent's write

        var report = await service.VerifyFileAsync(sourcePath);

        report.Silent.ShouldBeFalse();
        report.Verdict.ShouldNotBeNull();
        report.Verdict.Introduced.ShouldNotBeNull();
        report.Verdict.Introduced.ShouldNotBeEmpty();
        report.Text.ShouldNotBeNull();
        report.Text.ShouldContain("Thing.cs");
    }

    /// <summary>
    /// The regression this whole design exists for. A reload-based baseline compares two solutions
    /// with no shared Roslyn lineage, and <c>GetChanges</c> then reports every pre-existing error as
    /// introduced — measured at <c>introduced: 1, preexisting: 0</c> on two independent loads of the
    /// same broken code. Here nothing about the file changes between the two calls, so an honest
    /// guard says nothing at all, twice.
    /// </summary>
    [Fact]
    public async Task Never_Blames_The_Agent_For_Errors_That_Were_Already_There()
    {
        var (loader, workspace, sourcePath) = CreateSolution(BrokenSource);
        using var _ = workspace;
        using var service = CreateService(loader);

        var first = await service.VerifyFileAsync(sourcePath);
        first.Silent.ShouldBeTrue();

        // A write that does not change the text — the branch stays exactly as red as it was.
        File.WriteAllText(sourcePath, BrokenSource);
        var second = await service.VerifyFileAsync(sourcePath);

        second.Silent.ShouldBeTrue();
        second.Text.ShouldBeNull();
    }

    [Fact]
    public async Task An_Edit_That_Repairs_A_Broken_Branch_Says_Nothing()
    {
        var (loader, workspace, sourcePath) = CreateSolution(BrokenSource);
        using var _ = workspace;
        using var service = CreateService(loader);

        await service.VerifyFileAsync(sourcePath);
        File.WriteAllText(sourcePath, GreenSource);

        var report = await service.VerifyFileAsync(sourcePath);

        // Resolving errors is good news, and good news is not worth an agent's tokens.
        report.Silent.ShouldBeTrue();
    }

    /// <summary>
    /// A write that lands <em>while</em> a verification is running must still be verified on the next
    /// pass. Re-stat'ing the files after the compile would pair that file's new mtime with its old
    /// text in the snapshot, and the edit would then be skipped forever — silently, and permanently.
    /// </summary>
    [Fact]
    public async Task A_Write_During_Verification_Is_Not_Recorded_As_Already_Seen()
    {
        var (loader, workspace, sourcePath) = CreateSolution(GreenSource);
        using var _ = workspace;

        // Delegates to the real compiler: this test asserts on what gets REPORTED, not on call counts.
        var counting = new CountingVerificationService(
            new VerificationService(A.Fake<ILogger<VerificationService>>(), DiagnosticComputationService.CompilerOnly));
        using var service = CreateService(loader, counting);

        await service.VerifyFileAsync(sourcePath);          // baseline

        // First edit; hold the verification open and write again while it is in flight.
        File.WriteAllText(sourcePath, "public class Thing { public int Value() => 2; }");
        counting.Hold();
        var inFlight = service.VerifyFileAsync(sourcePath);
        await WaitForEntered(counting, "the verification call");

        File.WriteAllText(sourcePath, BrokenSource);        // lands mid-verification
        counting.Release();
        await inFlight;

        // The next pass must still see the broken text, not conclude it was already accounted for.
        var report = await service.VerifyFileAsync(sourcePath);

        report.Silent.ShouldBeFalse("the write that landed during verification was never re-read");
        report.Verdict.ShouldNotBeNull().Introduced.ShouldNotBeNull().ShouldNotBeEmpty();
    }

    /// <summary>
    /// Two projects in one solution are ONE baseline. Keying on the anchor <c>.csproj</c> instead
    /// would give each project its own snapshot of the whole solution — N× the memory, and each
    /// entry's resync would pick up the other's edits and report the same error again under whichever
    /// file happened to be written last.
    /// </summary>
    [Fact]
    public async Task An_Error_In_One_Project_Is_Reported_Once_Not_Again_Under_A_Sibling()
    {
        var (loader, workspace, paths) = CreateTwoProjectSolution();
        using var _ = workspace;
        using var service = CreateService(loader);

        // Both projects are seen while the tree is still green. This ordering is load-bearing: it is
        // what gives a per-.csproj keying TWO entries whose snapshots both predate the break, which
        // is the only arrangement in which the duplicate report appears.
        await service.VerifyFileAsync(paths.Side);
        await service.VerifyFileAsync(paths.Core);

        File.WriteAllText(paths.Core, BrokenSource);
        var first = await service.VerifyFileAsync(paths.Core);
        first.Silent.ShouldBeFalse("breaking Core must be reported once");

        // Now touch the OTHER project without breaking anything. Core's error is already accounted
        // for, so this write has nothing of its own to report — unless Side is carrying a separate,
        // stale snapshot of the same solution, which would rediscover Core's break and attribute it
        // to this edit.
        File.WriteAllText(paths.Side, "public class Side { public int Other() => 7; }");
        var second = await service.VerifyFileAsync(paths.Side);

        second.Silent.ShouldBeTrue("Core's error was already reported and is not this edit's doing");
    }

    [Fact]
    public async Task A_File_Outside_Any_Project_Is_Silent()
    {
        var orphan = Path.Combine(_root, "notes.cs");
        File.WriteAllText(orphan, "// nobody's project");

        var loader = A.Fake<IProjectLoader>();
        A.CallTo(() => loader.LoadForFileAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<LoadedProject?>(null));

        using var service = CreateService(loader);

        (await service.VerifyFileAsync(orphan)).Silent.ShouldBeTrue();
    }

    [Fact]
    public async Task Two_Concurrent_Calls_For_The_Same_Solution_Produce_One_Verification()
    {
        var (loader, workspace, sourcePath) = CreateSolution(GreenSource);
        using var _ = workspace;

        var counting = new CountingVerificationService();
        using var service = CreateService(loader, counting);

        await service.VerifyFileAsync(sourcePath);      // establish the baseline (no verification yet)
        counting.Calls.ShouldBe(0);

        File.WriteAllText(sourcePath, BrokenSource);

        counting.Hold();
        var first = service.VerifyFileAsync(sourcePath);

        // Bounded on purpose: if the guard never reaches VerifyAsync, this test must FAIL rather
        // than hang the whole suite — which is exactly what an unbounded wait did here once.
        await WaitForEntered(counting, "the first call");

        var second = service.VerifyFileAsync(sourcePath); // arrives while it is still in flight
        counting.Release();

        await Task.WhenAll(first, second);

        // The second call joined the first rather than starting a second compile.
        counting.Calls.ShouldBe(1);
    }

#pragma warning restore xUnit1051

    /// <summary>
    /// Counts <see cref="IVerificationService.VerifyAsync"/> calls and can hold the first one open,
    /// so a second call is guaranteed to arrive while the first is genuinely in flight.
    /// </summary>
    private sealed class CountingVerificationService : IVerificationService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IVerificationService? _inner;
        private TaskCompletionSource? _gate;
        private int _calls;

        /// <param name="inner">
        /// When supplied, the real verdict is produced by delegating — needed by any test that asserts
        /// on what was <em>reported</em> rather than on how many times verification ran.
        /// </param>
        public CountingVerificationService(IVerificationService? inner = null) => _inner = inner;

        public int Calls => Volatile.Read(ref _calls);

        public Task Entered => _entered.Task;

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public async Task<VerificationVerdict> VerifyAsync(
            Solution? baseline,
            Solution candidate,
            string? baseDirectory,
            int max = 20,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();

            if (_gate is not null)
            {
                await _gate.Task;
            }

            return _inner is null
                ? new VerificationVerdict { Compiles = true, ScopeComplete = true }
                : await _inner.VerifyAsync(baseline, candidate, baseDirectory, max, cancellationToken);
        }
    }
}
