using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using RoselineMCP.Benchmarks.Fixtures;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;

namespace RoselineMCP.Benchmarks;

/// <summary>
/// Benchmarks the <see cref="ISolutionAnalyzerService.AnalyzeSolutionAsync"/> service-layer
/// operation directly (no MCP protocol/JSON layer involved) against small and medium generated
/// fixture solutions, to give a real order-of-magnitude number for how long a full solution
/// analysis takes and how it scales with project/file count.
/// </summary>
/// <remarks>
/// The job is intentionally configured with a fixed, small warmup/iteration count rather than
/// BenchmarkDotNet's default pilot-driven job — each invocation does real MSBuild workspace
/// creation plus a real Roslyn compilation, so letting BenchmarkDotNet auto-tune toward its
/// usual ~15+ iterations per benchmark would make the whole suite take many minutes for very
/// little extra statistical value on a fixture this size.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class AnalyzeSolutionBenchmarks
{
    private string _tempRoot = null!;
    private ISolutionAnalyzerService _service = null!;
    private BenchmarkSolutionFixture.FixtureSolution _small = null!;
    private BenchmarkSolutionFixture.FixtureSolution _medium = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempRoot = Directory.CreateTempSubdirectory("RoselineMCP.Benchmarks_").FullName;

        var msBuildService = new MSBuildService(NullLogger<MSBuildService>.Instance);
        var codeFixProviderFactory = new CodeFixProviderFactory(NullLogger<CodeFixProviderFactory>.Instance);
        var filterService = new DiagnosticFilterService(codeFixProviderFactory);
        _service = new SolutionAnalyzerService(NullLogger<SolutionAnalyzerService>.Instance, msBuildService, filterService);

        // Small: a single project, a handful of files — a "quick sanity check" scale solution.
        _small = BenchmarkSolutionFixture.Create(_tempRoot, "Small", projectCount: 1, filesPerProject: 5);

        // Medium: several projects with more files each — enough to see cross-project overhead
        // without making the benchmark suite itself slow to run.
        _medium = BenchmarkSolutionFixture.Create(_tempRoot, "Medium", projectCount: 4, filesPerProject: 10);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Benchmark(Description = "AnalyzeSolution — small (1 project, 5 files)")]
    public Task<AnalyzeSolutionResponse> AnalyzeSmallSolution() =>
        _service.AnalyzeSolutionAsync(_small.SolutionPath);

    [Benchmark(Description = "AnalyzeSolution — medium (4 projects, 40 files)")]
    public Task<AnalyzeSolutionResponse> AnalyzeMediumSolution() =>
        _service.AnalyzeSolutionAsync(_medium.SolutionPath);
}
