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
        try { Directory.Delete(_root, true); } catch { /* ignored */ }

        GC.SuppressFinalize(this);
    }

    private const string GreenSource = "public class Thing { public int Value() => 1; }";
    private const string BrokenSource = "public class Thing { public int Value() => nope; }";

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
        await Task.WhenAny(counting.Entered, Task.Delay(TimeSpan.FromSeconds(30)));
        counting.Entered.IsCompleted.ShouldBeTrue("the first call never reached VerifyAsync");

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
        private TaskCompletionSource? _gate;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task Entered => _entered.Task;

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public async Task<VerificationVerdict> VerifyAsync(
            Solution? baseline,
            Solution candidate,
            int max = 20,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();

            if (_gate is not null)
            {
                await _gate.Task;
            }

            return new VerificationVerdict { Compiles = true, ScopeComplete = true };
        }
    }
}
