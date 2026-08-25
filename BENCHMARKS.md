# Benchmarks

RoselineMCP.Benchmarks is a [BenchmarkDotNet](https://benchmarkdotnet.org/) console app that
measures the core service-layer operations directly — `ISolutionAnalyzerService.AnalyzeSolutionAsync`,
`ICodeFixService.ApplyFixesAsync` and `IVerificationService.VerifyAsync` — bypassing the MCP
protocol/JSON layer entirely. It exists
to answer a question the rest of the docs don't: *how long does analysis/fixing actually take, and
how does it scale with project size?*

> **Three different benchmarks, three different questions:** this one measures **latency**;
> [`RoselineMCP.TokenBenchmark`](RoselineMCP.TokenBenchmark) measures how compact a single tool
> response is (the **85% median** headline; pooled, size-weighted: 93%); and
> [`docs/AGENT-BENCHMARK.md`](docs/AGENT-BENCHMARK.md) measures whether an AI agent doing a real
> task **end to end** actually spends fewer tokens with RoselineMCP installed (spoiler: on large
> codebases, yes — ~50% when forced onto the tools (the ceiling), ~13% in realistic self-directed
> use (n = 1); on tiny ones, break-even).

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
dotnet run -c Release --project RoselineMCP.Benchmarks -- --filter "*CheckCompilation*" "*VerifiedWriteOverhead*"
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

- **`CheckCompilationColdBenchmarks` / `CheckCompilationWarmBenchmarks` / `CheckCompilationAfterEditBenchmarks`**
  measure `check_compilation` — load the solution, then `VerifyAsync(baseline: null, …)` — in the
  three states a session actually passes through: the first call (cold MSBuild load), a repeat call
  with nothing changed (pure cache hit), and a call right after a file changed on disk (the real
  edit loop). Reported separately, because they differ by four orders of magnitude and quoting the
  cache-hit figure as "warm" would be quoting the wrong scenario.
- **`VerifiedWriteOverheadBenchmarks`** measures what the compile gate *costs* on the happy path:
  the same `edit_member` preview with and without verification, on an edit that introduces nothing.
  The un-verified arm uses a no-op verifier rather than an older build, so the only difference
  between the arms is the compilation itself.

All classes use a fixed, small job (`warmupCount: 2, iterationCount: 5`, single process) instead
of BenchmarkDotNet's default pilot-driven job. Each invocation does a real MSBuild workspace
creation plus a real Roslyn compilation, so letting BenchmarkDotNet auto-tune toward its usual
15+ iterations per benchmark would burn many minutes for negligible extra statistical value on
fixtures this size. The `AnalyzeSolution`/`ApplyFixes` pair runs in well under a minute; the
verification classes add roughly another minute, dominated by the cold-start measurements.

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

### check_compilation

Measured 2026-08-20 (same machine, .NET SDK 10.0.400, BenchmarkDotNet v0.15.8).

| State | Small (1 project, 5 files) | Medium (4 projects, 40 files) |
|---|---:|---:|
| **Cold** — first call of a session (median; mean/StdDev 833±593 ms and 952±500 ms) | 610 ms | 751 ms |
| **After an on-disk edit** — the real edit loop | 413 ms | 625 ms |
| **Repeat call, nothing changed** — pure cache hit | 32 µs | 108 µs |

**Target: warm < 1 s. Met** — 413 ms / 625 ms after a real edit, which is the number that matters.
The 32–108 µs row is honest but answers a question nobody asks: an agent calls this tool *because*
it just edited a file, and that edit invalidates the workspace. Do not quote it as the headline.

The cold row is deliberately noisy (5 iterations, `RunStrategy.ColdStart`, no warmup — each
measurement is a genuine first call, and StdDev is as large as the mean). Medians are shown because
the mean is not representative at that spread.

### Verified-write overhead (happy path)

`edit_member` preview against the medium fixture, warm workspace, on an edit that introduces
nothing — the overwhelmingly common case.

| Arm | Mean | Ratio |
|---|---:|---:|
| WITHOUT verification | 2.00 ms | 1.00 |
| WITH verification, novel edit (real compile) | 8.17 ms | 4.10 |
| WITH verification, repeated edit (identical text) | 8.46 ms | 4.25 |

**Target: overhead < 500 ms. Met** — the gate adds **~6 ms** to a member-level edit. The 4× ratio
looks alarming only because the un-gated baseline is 2 ms; the absolute figure is what an agent
feels, and 6 ms against the 30–90 s `dotnet build` it removes is not a trade-off that needs
defending.

**Two findings worth keeping, both of which the numbers contradict an obvious guess about:**

1. **Re-applying identical text is not cheaper than a novel edit** (8.46 ms vs 8.17 ms — within
   noise). Roslyn's version stamps are identity-based, not content-based, so `WithDocumentText`
   yields a new document version even for byte-identical text and the *candidate* compilation always
   misses the cache. The baseline cache is real and does hit (see
   `VerificationServiceCacheTests.An_Unchanged_Baseline_Is_Compiled_Once_Across_Two_Verifications`);
   it is the candidate side that is always paid. Content-addressing the candidate is a possible
   future saving, not something the current design claims.
2. **The overhead is not the compilation of the whole scope.** 6 ms for a 4-project fixture is far
   below the ~600 ms a cold load of the same fixture costs, because the workspace stays warm and
   Roslyn recompiles incrementally from the changed document.

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
