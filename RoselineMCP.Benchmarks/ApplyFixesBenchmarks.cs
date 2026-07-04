using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using RoselineMCP.Benchmarks.Fixtures;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;

namespace RoselineMCP.Benchmarks;

/// <summary>
/// Benchmarks the <see cref="ICodeFixService.ApplyFixesAsync"/> service-layer operation
/// (diagnostic discovery, code-fix-provider lookup, fix application, formatting and diff
/// generation) directly against the first project of small/medium generated fixture solutions.
/// </summary>
/// <remarks>
/// Runs with <c>previewOnly: true</c> so the benchmark never writes to disk — each iteration
/// re-loads the same unmodified fixture project from disk via a fresh workspace (exactly what
/// <see cref="CodeFixService"/> does per call), keeping every invocation's starting state
/// identical without needing to reset files between iterations.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class ApplyFixesBenchmarks
{
    private static readonly List<string> DiagnosticIds = ["CS0219"];

    private string _tempRoot = null!;
    private ICodeFixService _service = null!;
    private BenchmarkSolutionFixture.FixtureSolution _small = null!;
    private BenchmarkSolutionFixture.FixtureSolution _medium = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempRoot = Directory.CreateTempSubdirectory("RoselineMCP.Benchmarks_").FullName;

        var msBuildService = new MSBuildService(NullLogger<MSBuildService>.Instance);
        var codeFixProviderFactory = new CodeFixProviderFactory(NullLogger<CodeFixProviderFactory>.Instance);
        var projectLoader = new ProjectLoader(NullLogger<ProjectLoader>.Instance, msBuildService);
        var analyzerService = new SolutionAnalyzerService(
            NullLogger<SolutionAnalyzerService>.Instance,
            msBuildService,
            new DiagnosticFilterService(codeFixProviderFactory),
            projectLoader);
        var diffService = new DiffService();

        _service = new CodeFixService(
            NullLogger<CodeFixService>.Instance,
            analyzerService,
            codeFixProviderFactory,
            diffService,
            projectLoader);

        // Same fixtures as AnalyzeSolutionBenchmarks; ApplyFixes targets a single project
        // (the first one), matching how the tool is used in practice.
        _small = BenchmarkSolutionFixture.Create(_tempRoot, "Small", projectCount: 1, filesPerProject: 5);
        _medium = BenchmarkSolutionFixture.Create(_tempRoot, "Medium", projectCount: 4, filesPerProject: 10);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Benchmark(Description = "ApplyFixes (preview) — small project (5 files)")]
    public Task<ApplyFixesResponse> ApplyFixesSmallProject() =>
        _service.ApplyFixesAsync(_small.FirstProjectPath, DiagnosticIds, previewOnly: true);

    [Benchmark(Description = "ApplyFixes (preview) — medium project (10 files)")]
    public Task<ApplyFixesResponse> ApplyFixesMediumProject() =>
        _service.ApplyFixesAsync(_medium.FirstProjectPath, DiagnosticIds, previewOnly: true);
}
