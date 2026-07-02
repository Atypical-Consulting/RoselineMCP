# Benchmarks

RoselineMCP.Benchmarks is a [BenchmarkDotNet](https://benchmarkdotnet.org/) console app that
measures the two core service-layer operations directly — `ISolutionAnalyzerService.AnalyzeSolutionAsync`
and `ICodeFixService.ApplyFixesAsync` — bypassing the MCP protocol/JSON layer entirely. It exists
to answer a question the rest of the docs don't: *how long does analysis/fixing actually take, and
how does it scale with project size?*

## Running it

```bash
dotnet run -c Release --project RoselineMCP.Benchmarks
```

Always run with `-c Release` — BenchmarkDotNet refuses a Debug build by default, and Debug numbers
aren't representative anyway.

To run a subset (BenchmarkDotNet's `--filter` uses glob patterns against `Type.Method`):

```bash
dotnet run -c Release --project RoselineMCP.Benchmarks -- --filter "*AnalyzeSolution*"
dotnet run -c Release --project RoselineMCP.Benchmarks -- --filter "*ApplyFixes*"
```

Results (full text report, HTML, CSV, and a GitHub-flavored markdown table) are written to
`RoselineMCP.Benchmarks/BenchmarkDotNet.Artifacts/results/` (git-ignored — regenerate locally
rather than committing them).

## What's being measured

Each benchmark class generates its own throwaway fixture solution on disk in `[GlobalSetup]` (see
`RoselineMCP.Benchmarks/Fixtures/BenchmarkSolutionFixture.cs`) rather than depending on a
checked-in "realistic" solution:

| Fixture  | Shape                                    | Purpose                                   |
|----------|-------------------------------------------|--------------------------------------------|
| `Small`  | 1 project, 5 source files                 | Baseline / quick sanity-check scale        |
| `Medium` | 4 projects, 10 files each (40 files total)| Cross-project overhead, still fast to run  |

Every generated project is a plain SDK-style class library with **no `PackageReference`s**, so
`MSBuildWorkspace` can design-time-build it without a prior `dotnet restore` (the same approach
`CodeFixServiceIntegrationTests` uses). Each file contains one intentional `CS0219` ("unused local
variable") diagnostic, giving `AnalyzeSolution` something real to find and `ApplyFixes` something
real to fix, without needing any analyzer packages restored.

- **`AnalyzeSolutionBenchmarks`** calls `AnalyzeSolutionAsync` against the `Small` and `Medium`
  solution's `.sln` file — full workspace load + compilation + diagnostic collection across all
  projects.
- **`ApplyFixesBenchmarks`** calls `ApplyFixesAsync` against the first project of each fixture with
  `ids: ["CS0219"]` and `previewOnly: true` — workspace load, diagnostic lookup, code-fix-provider
  application, formatting, and diff generation, without writing to disk (so repeated iterations
  always start from the same on-disk state).

Both classes use a fixed, small job (`warmupCount: 2, iterationCount: 5`, single process) instead
of BenchmarkDotNet's default pilot-driven job. Each invocation does a real MSBuild workspace
creation plus a real Roslyn compilation, so letting BenchmarkDotNet auto-tune toward its usual
15+ iterations per benchmark would burn many minutes for negligible extra statistical value on
fixtures this size. The whole suite (4 benchmarks) runs in well under a minute.

## Results

Measured on: macOS Tahoe 26.5.1, Apple M5 Max, .NET SDK 10.0.301, BenchmarkDotNet v0.15.8,
`RoselineMCP.Benchmarks` in Release, run on 2026-07-02. These are **directional, single-machine
numbers** meant to catch order-of-magnitude regressions — not a promise of throughput on any
particular machine. Re-run locally (or in CI) before relying on them; update this table whenever
a change to the analyzer/fix pipeline could plausibly move these numbers, and at minimum once per
release.

### AnalyzeSolution

| Method                                          | Mean     | Error     | StdDev   | Gen0      | Gen1      | Allocated |
|--------------------------------------------------|---------:|----------:|---------:|----------:|----------:|----------:|
| AnalyzeSolution — small (1 project, 5 files)     | 390.6 ms |  18.11 ms |  4.70 ms | 4000.0000 | 1000.0000 |  37.24 MB |
| AnalyzeSolution — medium (4 projects, 40 files)  | 586.1 ms | 119.71 ms | 31.09 ms | 9000.0000 | 1000.0000 |   76.5 MB |

### ApplyFixes (preview mode)

| Method                                              | Mean     | Error     | StdDev   | Gen0      | Gen1      | Allocated |
|-------------------------------------------------------|---------:|----------:|---------:|----------:|----------:|----------:|
| ApplyFixes (preview) — small project (5 files)        | 424.2 ms |  15.06 ms |  2.33 ms | 5000.0000 | 1000.0000 |  40.02 MB |
| ApplyFixes (preview) — medium project (10 files)      | 495.2 ms | 105.50 ms | 16.33 ms | 9000.0000 | 1000.0000 |  73.84 MB |

### Reading these numbers

- The bulk of the cost (~350-400ms baseline even on a 1-project/5-file solution) is MSBuild
  workspace creation and Roslyn's first compilation for the process — not the diagnostic-filtering
  or fix-application logic itself, which is comparatively cheap. Don't extrapolate "small→medium"
  scaling linearly to much larger solutions; the fixed workspace/compilation overhead amortizes
  differently at real-world scale (hundreds of projects), which this suite intentionally does not
  attempt to simulate.
- `StdDev`/`Error` are wide relative to the mean (5 iterations, no process isolation between
  iterations) — treat these as ballpark figures, not precise measurements. Increase
  `iterationCount` in the `[SimpleJob]` attributes locally if you need tighter confidence
  intervals for a specific investigation.
- No documented "scale limit" exists yet for very large solutions (100+ projects) — this suite
  only exercises small/medium fixtures by design (see "Keep it fast" below). If you need numbers
  for a specific large solution, point `AnalyzeSolutionAsync`/`ApplyFixesAsync` at it directly (or
  extend `BenchmarkSolutionFixture` with a larger fixture) and measure separately.

## Keep it fast

This suite is meant to run in well under a minute so it's actually run, not skipped. If you add
benchmarks or fixtures, keep that budget in mind:

- Prefer growing `filesPerProject`/`projectCount` moderately over adding many new benchmark
  methods — each `[Benchmark]` method costs a full warmup+iteration cycle.
- Keep the explicit `[SimpleJob(warmupCount: ..., iterationCount: ...)]` configuration rather than
  falling back to BenchmarkDotNet's default pilot-driven job, which will happily spend minutes
  chasing statistical significance on a workload this cheap.
