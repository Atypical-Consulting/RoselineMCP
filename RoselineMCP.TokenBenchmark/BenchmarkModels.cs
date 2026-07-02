using Microsoft.CodeAnalysis;
using RoselineMCP.Interfaces;

namespace RoselineMCP.TokenBenchmark;

/// <summary>A character + token measurement of one string.</summary>
public record Measure(int Chars, int Tokens);

/// <summary>One measured task: the tool's output vs. the source an agent would otherwise read.</summary>
public record TaskRow(
    string Target,
    Measure WholeFile,                 // B1: the whole file(s) an agent would open
    Measure? Targeted,                 // B2: just the relevant lines (grep -C3 model); null when N/A
    Measure Tool,                      // the tool's actual JSON output
    double SavingsVsWholeFilePct,
    double? SavingsVsTargetedPct);

public record SuiteAggregate(
    int Count,
    int WeakOrNegativeCount,           // rows where the tool saved < 25% vs whole-file (honesty flag)
    double MedianSavingsVsWholeFile,
    double MeanSavingsVsWholeFile,
    double MinSavingsVsWholeFile,
    double MaxSavingsVsWholeFile,
    long TotalWholeFileTokens,
    long TotalToolTokens,
    double PooledSavingsVsWholeFile,   // 1 - sum(tool)/sum(whole): weights by size
    double? MedianSavingsVsTargeted,
    double? PooledSavingsVsTargeted);

public record SuiteResult(
    string Id,
    string Tool,
    string Title,
    string Description,
    string BaselineNote,
    bool Systematic,                   // true = swept over all/most candidates (not hand-picked)
    List<TaskRow> Rows,
    SuiteAggregate Aggregate);

public record BenchmarkMetadata(
    string GeneratedAt,
    string Commit,
    string Solution,
    string TargetProject,
    string Tokenizer,
    string ToolOutputFormat,
    int FilesSwept,
    int SymbolsSwept);

public record Headline(
    double PooledSavingsReadTools,
    double MedianSavingsReadTools,
    long TotalBaselineTokens,
    long TotalToolTokens,
    string Statement);

public record BenchmarkReport(
    BenchmarkMetadata Metadata,
    List<string> Methodology,
    List<string> Limitations,
    Headline Headline,
    List<SuiteResult> Suites);

/// <summary>
/// Test-style loader that hands the navigation/edit services the already-loaded solution snapshot,
/// so the benchmark performs the expensive MSBuild solution load exactly once instead of per call.
/// The throwaway workspace only satisfies <see cref="LoadedProject"/>'s disposal contract; the real
/// solution/project (whose backing workspace is kept alive by the caller) drive every measurement.
/// </summary>
public sealed class SharedProjectLoader : IProjectLoader
{
    private readonly Solution _solution;
    private readonly Project _project;

    public SharedProjectLoader(Solution solution, Project project)
    {
        _solution = solution;
        _project = project;
    }

    public Task<LoadedProject> LoadAsync(string project, CancellationToken cancellationToken = default) =>
        Task.FromResult(new LoadedProject(new AdhocWorkspace(), _solution, _project));
}
