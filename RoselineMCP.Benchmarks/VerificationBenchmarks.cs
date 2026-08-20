using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RoselineMCP.Benchmarks.Fixtures;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;

namespace RoselineMCP.Benchmarks;

/// <summary>
/// Shared wiring for the verification benchmarks: the same services production DI composes, plus
/// the fixture solutions the rest of the suite uses.
/// </summary>
internal static class VerificationBenchmarkWiring
{
    /// <summary>A caching loader, exactly as <c>Program.cs</c> registers it.</summary>
    public static IProjectLoader CreateLoader()
    {
        var msBuildService = new MSBuildService(NullLogger<MSBuildService>.Instance);
        var inner = new ProjectLoader(NullLogger<ProjectLoader>.Instance, msBuildService);
        return new CachingProjectLoader(
            inner,
            Options.Create(new RoselineMcpOptions()),
            NullLogger<CachingProjectLoader>.Instance);
    }

    /// <summary>Compiler-only, exactly as <c>Program.cs</c> registers it.</summary>
    public static VerificationService CreateVerification() =>
        new(NullLogger<VerificationService>.Instance, DiagnosticComputationService.CompilerOnly);

    public static CodeEditService CreateEditService(IProjectLoader loader, IVerificationService verification) =>
        new(NullLogger<CodeEditService>.Instance, loader, new DiffService(), verification);
}

/// <summary>
/// <c>check_compilation</c> on a <b>cold</b> workspace — the first call of a session, which pays the
/// full MSBuild design-time load.
/// </summary>
/// <remarks>
/// Reported separately from the warm case on purpose. The saving this tool sells comes from a warm,
/// incrementally-compilable workspace, and every session starts without one; quoting only the warm
/// figure would be quoting the best case as if it were the typical one. <see cref="RunStrategy.ColdStart"/>
/// with one operation per iteration is what makes each measurement a genuine first call.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.ColdStart, launchCount: 1, warmupCount: 0, iterationCount: 5, invocationCount: 1)]
public class CheckCompilationColdBenchmarks
{
    private string _tempRoot = null!;
    private BenchmarkSolutionFixture.FixtureSolution _small = null!;
    private BenchmarkSolutionFixture.FixtureSolution _medium = null!;
    private IProjectLoader _loader = null!;
    private VerificationService _verification = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempRoot = Directory.CreateTempSubdirectory("RoselineMCP.VerifyBench_").FullName;
        _small = BenchmarkSolutionFixture.Create(_tempRoot, "Small", projectCount: 1, filesPerProject: 5);
        _medium = BenchmarkSolutionFixture.Create(_tempRoot, "Medium", projectCount: 4, filesPerProject: 10);
    }

    // A fresh loader and a fresh verification cache per iteration: nothing may carry over, or this
    // stops measuring a cold start and starts measuring a warm one.
    [IterationSetup]
    public void IterationSetup()
    {
        _loader = VerificationBenchmarkWiring.CreateLoader();
        _verification = VerificationBenchmarkWiring.CreateVerification();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _verification.Dispose();
        (_loader as IDisposable)?.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<VerificationVerdict> CheckAsync(string solutionPath)
    {
        using var loaded = await _loader.LoadAsync(solutionPath);
        return await _verification.VerifyAsync(baseline: null, loaded.Solution);
    }

    [Benchmark(Description = "check_compilation — COLD, small solution (1 project, 5 files)")]
    public Task<VerificationVerdict> ColdSmall() => CheckAsync(_small.SolutionPath);

    [Benchmark(Description = "check_compilation — COLD, medium solution (4 projects, 40 files)")]
    public Task<VerificationVerdict> ColdMedium() => CheckAsync(_medium.SolutionPath);
}

/// <summary>
/// <c>check_compilation</c> on a <b>warm</b> workspace — every call after the first, which is what
/// the edit loop actually pays and where the sub-second target lives.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class CheckCompilationWarmBenchmarks
{
    private string _tempRoot = null!;
    private BenchmarkSolutionFixture.FixtureSolution _small = null!;
    private BenchmarkSolutionFixture.FixtureSolution _medium = null!;
    private IProjectLoader _loader = null!;
    private VerificationService _verification = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempRoot = Directory.CreateTempSubdirectory("RoselineMCP.VerifyBench_").FullName;
        _small = BenchmarkSolutionFixture.Create(_tempRoot, "Small", projectCount: 1, filesPerProject: 5);
        _medium = BenchmarkSolutionFixture.Create(_tempRoot, "Medium", projectCount: 4, filesPerProject: 10);

        _loader = VerificationBenchmarkWiring.CreateLoader();
        _verification = VerificationBenchmarkWiring.CreateVerification();

        // Pay both cold loads here, outside the measurement.
        CheckAsync(_small.SolutionPath).GetAwaiter().GetResult();
        CheckAsync(_medium.SolutionPath).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _verification.Dispose();
        (_loader as IDisposable)?.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<VerificationVerdict> CheckAsync(string solutionPath)
    {
        using var loaded = await _loader.LoadAsync(solutionPath);
        return await _verification.VerifyAsync(baseline: null, loaded.Solution);
    }

    [Benchmark(Description = "check_compilation — WARM, small solution (1 project, 5 files)")]
    public Task<VerificationVerdict> WarmSmall() => CheckAsync(_small.SolutionPath);

    [Benchmark(Description = "check_compilation — WARM, medium solution (4 projects, 40 files)")]
    public Task<VerificationVerdict> WarmMedium() => CheckAsync(_medium.SolutionPath);
}

/// <summary>
/// <c>check_compilation</c> on a warm workspace whose source has <b>actually changed on disk</b> —
/// the real edit-loop shape.
/// </summary>
/// <remarks>
/// This is the number that matters, and it is not the one <see cref="CheckCompilationWarmBenchmarks"/>
/// produces. That class calls twice with nothing changed, so the workspace fingerprint matches, the
/// verification cache hits, and the measurement is of two dictionary lookups — a real figure, but for
/// a question nobody asks. An agent calls this tool *because* it just edited a file, which invalidates
/// the workspace and forces a genuine reload and compile. Quoting the cache-hit figure as "warm" would
/// be quoting the wrong scenario, so both are reported.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5, invocationCount: 1)]
public class CheckCompilationAfterEditBenchmarks
{
    private string _tempRoot = null!;
    private BenchmarkSolutionFixture.FixtureSolution _small = null!;
    private BenchmarkSolutionFixture.FixtureSolution _medium = null!;
    private IProjectLoader _loader = null!;
    private VerificationService _verification = null!;
    private int _edits;

    [GlobalSetup]
    public void Setup()
    {
        _tempRoot = Directory.CreateTempSubdirectory("RoselineMCP.VerifyBench_").FullName;
        _small = BenchmarkSolutionFixture.Create(_tempRoot, "Small", projectCount: 1, filesPerProject: 5);
        _medium = BenchmarkSolutionFixture.Create(_tempRoot, "Medium", projectCount: 4, filesPerProject: 10);

        _loader = VerificationBenchmarkWiring.CreateLoader();
        _verification = VerificationBenchmarkWiring.CreateVerification();

        // Pay both cold loads outside the measurement; every measured call then starts from a warm
        // server that has just had a file changed under it.
        CheckAsync(_small.SolutionPath).GetAwaiter().GetResult();
        CheckAsync(_medium.SolutionPath).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _verification.Dispose();
        (_loader as IDisposable)?.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Rewrites one source file so both the workspace and the verification cache miss.</summary>
    private void EditOneFile(BenchmarkSolutionFixture.FixtureSolution fixture)
    {
        var path = Path.Combine(Path.GetDirectoryName(fixture.FirstProjectPath)!, "Class000.cs");
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("Value * 2;", $"Value * 2 + {_edits++ % 7} - {_edits % 7};"));
    }

    private async Task<VerificationVerdict> CheckAsync(string solutionPath)
    {
        using var loaded = await _loader.LoadAsync(solutionPath);
        return await _verification.VerifyAsync(baseline: null, loaded.Solution);
    }

    [IterationSetup(Target = nameof(AfterEditSmall))]
    public void EditSmall() => EditOneFile(_small);

    [IterationSetup(Target = nameof(AfterEditMedium))]
    public void EditMedium() => EditOneFile(_medium);

    [Benchmark(Description = "check_compilation — after an on-disk edit, small solution (1 project, 5 files)")]
    public Task<VerificationVerdict> AfterEditSmall() => CheckAsync(_small.SolutionPath);

    [Benchmark(Description = "check_compilation — after an on-disk edit, medium solution (4 projects, 40 files)")]
    public Task<VerificationVerdict> AfterEditMedium() => CheckAsync(_medium.SolutionPath);
}

/// <summary>
/// What verification <b>costs</b> on the happy path: the same <c>edit_member</c> preview, with the
/// gate and without it, on an edit that introduces nothing.
/// </summary>
/// <remarks>
/// Reporting only the case where the gate catches something would be dishonest by this repository's
/// standards. Nearly every edit an agent makes is fine, so the overhead paid on those edits — not
/// the saving on the rare broken one — is the number that decides whether this feature is worth
/// shipping. The unverified arm uses a no-op verifier rather than an older build, so the only
/// difference between the two arms is the compilation itself.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5, invocationCount: 1)]
public class VerifiedWriteOverheadBenchmarks
{
    // Fully qualified: the medium fixture has four projects, each with its own Class000, and the
    // resolver rightly refuses an ambiguous name.
    private const string Symbol = "RoselineMCP.Benchmarks.Generated.MediumProject00.Class000.DoubleValue";

    /// <summary>
    /// The same edit text on every call. Measured 2026-08-20: this is <b>not</b> cheaper than a novel
    /// edit — Roslyn's version stamps are identity-based, not content-based, so re-applying identical
    /// text still yields a new document version and the candidate compilation misses the cache. The
    /// baseline side is what the cache serves; the candidate side is always paid.
    /// </summary>
    private const string RepeatedSource = "public int DoubleValue() => Value + Value;";

    private string _tempRoot = null!;
    private BenchmarkSolutionFixture.FixtureSolution _medium = null!;
    private IProjectLoader _loader = null!;
    private VerificationService _verification = null!;
    private ICodeEditService _verified = null!;
    private ICodeEditService _unverified = null!;
    private int _counter;

    /// <summary>A verifier that compiles nothing — the "before this feature" arm.</summary>
    private sealed class NoVerification : IVerificationService
    {
        public Task<VerificationVerdict> VerifyAsync(
            Solution? baseline, Solution candidate, int max = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationVerdict());
    }

    /// <summary>
    /// A different member body on every call, so the candidate solution is genuinely new and its
    /// compilation cannot be served from the verification cache.
    /// </summary>
    private string NovelSource() => $"public int DoubleValue() => Value + Value + {_counter++ % 4} - {_counter % 4};";

    [GlobalSetup]
    public void Setup()
    {
        _tempRoot = Directory.CreateTempSubdirectory("RoselineMCP.VerifyBench_").FullName;
        _medium = BenchmarkSolutionFixture.Create(_tempRoot, "Medium", projectCount: 4, filesPerProject: 10);

        // One shared, warm loader: both arms must see the same workspace state, or the comparison
        // is between a warm call and a cold one rather than between gated and ungated. Nothing is
        // ever written (previewOnly), so the workspace itself stays valid across every invocation.
        _loader = VerificationBenchmarkWiring.CreateLoader();
        _verification = VerificationBenchmarkWiring.CreateVerification();
        _verified = VerificationBenchmarkWiring.CreateEditService(_loader, _verification);
        _unverified = VerificationBenchmarkWiring.CreateEditService(_loader, new NoVerification());

        _verified.EditMemberAsync(
            _medium.FirstProjectPath, Symbol, "replace", RepeatedSource, previewOnly: true).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _verification.Dispose();
        (_loader as IDisposable)?.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Benchmark(Baseline = true, Description = "edit_member (preview) — WITHOUT verification")]
    public Task<EditMemberResponse> WithoutVerification() =>
        _unverified.EditMemberAsync(_medium.FirstProjectPath, Symbol, "replace", NovelSource(), previewOnly: true);

    [Benchmark(Description = "edit_member (preview) — WITH verification, novel edit (real compile)")]
    public Task<EditMemberResponse> WithVerificationNovelEdit() =>
        _verified.EditMemberAsync(_medium.FirstProjectPath, Symbol, "replace", NovelSource(), previewOnly: true);

    [Benchmark(Description = "edit_member (preview) — WITH verification, repeated edit (identical text)")]
    public Task<EditMemberResponse> WithVerificationRepeatedEdit() =>
        _verified.EditMemberAsync(_medium.FirstProjectPath, Symbol, "replace", RepeatedSource, previewOnly: true);
}
