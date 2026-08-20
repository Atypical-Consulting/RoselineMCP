# End-to-end agent benchmark — does RoselineMCP save tokens *in practice*?

[`BENCHMARKS.md`](../BENCHMARKS.md) measures raw service latency, and the
[token-savings benchmark](../RoselineMCP.TokenBenchmark) measures a single number in isolation: the
tokens one tool call emits versus reading the corresponding file (a **median 85%** reduction per
task on this repo's own source; pooled, size-weighted: 93%). That's a *unit* measurement. It answers
"how compact is one tool response?" — not "does an AI agent, doing a real task end to end, actually
consume fewer tokens because RoselineMCP is installed?"

This document answers the end-to-end question with a controlled A/B test, and reports the result
honestly — including where the MCP does **not** help.

> **How to read the headline numbers.** The **~50%** end-to-end reduction below comes from the
> *forced-use* cell (`Read`/`Grep`/`Glob` removed), so it is a **ceiling**, not the expected
> everyday saving. In the realistic mode — the MCP merely available and the model self-directing
> (see the [follow-up](#follow-up--making-the-model-actually-use-the-tools)) — the measured saving
> on the same large-repo task is **~13%** (437k vs. 500k tokens, **n = 1**).

## Method

Each cell is one real Claude Code session (`claude -p`, Claude Sonnet), run on a **fresh git clone**
of the target repo, differing only in which tools are available:

- **Control** — vanilla Claude Code (`Read`/`Grep`/`Glob`), **no MCP servers**.
- **+MCP** — the same, plus RoselineMCP. In the *comprehension* rows the agent was **forced** to
  navigate through RoselineMCP (`Read`/`Grep`/`Glob` removed) so the tools are actually exercised;
  in the *greenfield/brownfield* rows the MCP was merely **available** (the realistic default).

Quality is an objective gate — `dotnet test` / injected acceptance tests / a graded structural
answer — not a subjective judgement. "Tokens" is total input tokens (regular + cache) from the
session's reported usage. **n = 1 per cell**: treat single-digit-percent gaps as noise; the 2×
gap on the large repo is not noise.

## Results

Quality was **identical in every cell** — every run produced passing tests / the correct answer.
So RoselineMCP was never a *correctness* factor here; the only variable that moved is token cost.

| Scenario | Target files | Control (tokens / $) | +MCP (tokens / $) | MCP actually used? | Token Δ |
|---|---|---|---|---|---|
| **Greenfield** — build a small library from a spec | brand new | 453,554 / $0.42 | 440,268 / $0.39 *(available)* | **no — 0 calls** | −3% (noise) |
| **Brownfield** — add a feature to a *small* solution | ~30-line | 691,499 / $0.61 | 457,918 / $0.46 *(available)* | **no — 0 calls** | −34%, but see below |
| **Comprehension** — map a *small* solution | ~30-line | 453,722 / $0.46 | 350,189 / $0.64 *(forced)* | yes — 11 calls | −23% tok / +37% $ |
| **Comprehension** — map a *large* solution (RoselineMCP) | 700-line | 499,675 / $0.50 | **239,357 / $0.37** *(forced)* | yes — 3 calls | **−52% tok / −27% $** |

## What it means

1. **The benefit scales with file size — that is the whole story.** On large source files
   (RoselineMCP's own ~700-line services) navigating structurally instead of reading whole files
   **roughly halved** the tokens for the same correct answer, in half the turns (3 tool calls
   total) — under forced use, i.e. the ceiling. This is where the unit benchmark's per-call savings
   (89% median) convert into real end-to-end savings. On tiny files it is
   **break-even to slightly worse** — reading a 30-line file is already cheap, so the fixed cost of
   the MCP's tool schemas plus per-call round-trips cancels the saving.

2. **The model does not reach for the MCP on its own** on small/simple work. In the greenfield and
   brownfield rows the MCP was installed *and* the system prompt nudged the agent to prefer it — yet
   it made **zero** RoselineMCP calls and used `Read`. (The brownfield −34% was therefore run-to-run
   variance between two `Read`-based runs, **not** the MCP.) The tools were only exercised when
   `Read`/`Grep` were removed. **An MCP that isn't invoked delivers nothing.**

3. **Greenfield sees no effect**, as expected — fresh code has no existing structure to navigate.

## Takeaways

RoselineMCP is a **large-codebase navigation tool**. Used against big files it delivers a real,
measured ~50% end-to-end token reduction at equal quality **when forced to navigate through the
tools** — that is the ceiling; realistic self-directed use lands at ~13% (n = 1, see the follow-up
below). On small repos it is roughly break-even; for greenfield work it is irrelevant. The
highest-leverage improvement is **adoption** — making the
tool descriptions actively steer the model to prefer structural navigation over reading large files,
so the win materialises in normal use rather than only when the agent is forced.

## Follow-up — making the model actually use the tools

The finding above (the model won't call the MCP by default) turned out to be fixable in-product.
Three levers, each tested on the large-repo comprehension task in **plain mode** — the MCP available,
**no external nudge**, so it reflects real product behavior:

| Build | roseline calls (unprompted) | failed on `project` | Read | Tokens |
|---|---|---|---|---|
| Baseline — neutral descriptions, no server instructions | **0** | — | many | — |
| + server `instructions` + decision-rule descriptions | 8 | **4** | 0 | 594k |
| + optional `project` / `.sln` path accepted | **3** | **0** | 0 | **437k** |

Reading the arc: an MCP that is merely *installed* is invisible — the model reaches for `Read`. What
flips it:

1. **Server-level `instructions`** stating a decision policy ("prefer these tools over reading whole
   files, especially on large ones") — the single biggest lever; the client injects it every session.
2. **Descriptions written as decision rules** ("prefer over Read/Grep to answer 'where is this
   used'"), not feature lists.
3. **Low-friction arguments.** With the tools adopted but `project` *required*, the model wasted 4
   calls guessing it (it naturally tried the `.sln` path, which used to fail) and burned more tokens
   than reading. Making `project` optional (auto-discovered) and accepting a `.sln` path collapsed it
   to 3 clean calls that now beat vanilla `Read` (437k vs 500k, **~13%**, n = 1) — all self-directed.

Even so, self-directed use (437k) doesn't reach the *forced-minimal* path (239k, the ~50% ceiling):
fixed tool-schema/instruction overhead plus a little extra exploration remain, so ~13% is the
realistic figure today. The tools are a large-codebase win; the steering is what makes the model
take it.

## Planned: does the compile gate change *quality*? (pre-registered, NOT YET RUN)

Everything above measures cost. The line that matters most in it is this one: **quality was
identical in every cell.** RoselineMCP has never been a correctness factor, only a cost one — which
is a ceiling, not a plateau, and it is the ceiling the compile-verified edit loop (#133) exists to
break.

This section is written **before the experiment runs**, so the criterion cannot be chosen after
seeing the data. It records what would count as the bet paying off, and what would count as it
failing. Nothing below is a result.

> **Status: not yet run.** The protocol is pre-registered here; the results table is deliberately
> empty. Do not cite anything from this section as a measurement.

### Falsification criterion — written first

The bet is that refusing writes which introduce compiler errors makes an agent *finish in a working
state more often*, not merely more cheaply.

**The bet pays off only if the treatment improves at least one of:**

1. **Broken final states** — runs whose final tree does not compile. Treatment < control.
2. **Turns to green** — assistant turns from the first edit until the tree compiles again.
   Treatment < control.

**The bet has failed if neither moves.** In that case this document says so plainly, in the same
words it uses here, and the feature is a cost/ergonomics change rather than a correctness one. A
token saving alone does **not** rescue it: cost is what the rest of this document already measures,
and if that is all that moves, the honest conclusion is that the gate did not change quality.

An outcome worth naming in advance because it is neither of the above: the gate could *raise* turns
to green by refusing an intermediate state the agent would have repaired on its own two turns later.
That would be a real cost, and it must be reported as one rather than folded into "no effect".

### The task

**A public signature change consumed by another project**, on a **multi-project** fixture.

Both halves of that are load-bearing:

- *Public signature change*, because that is the edit whose breakage an agent cannot see from the
  file it is editing — exactly what a file-scoped or project-scoped check misses.
- *Multi-project*, because on a single-project repo the gate has nothing to catch: the compiler
  error appears in the same project the agent just edited and the very next build would show it.
  Measuring there would be measuring the gate where it cannot fail, which is how a feature gets a
  flattering number that means nothing.

The fixture must therefore have a consumer project that the agent is *not* asked to touch, so
forgetting it is a realistic failure rather than a contrived one.

### Protocol

- **n ≥ 3 per cell.** The existing tables in this document are n = 1 and say so; a quality claim
  cannot rest on that, because the outcome is a small integer count and a single run cannot
  distinguish 0 from 1 broken states.
- **Control** — RoselineMCP available, compile gate **off**
  (`ROSELINE_RoselineMCP__ConfirmDestructiveWrites=false` and `allowIntroducedErrors: true`), so the
  only variable is the gate itself and not the presence of the tools.
- **Treatment** — the same, gate on (the shipped default).
- **Record per run:** does the final state compile (objective, `dotnet build`); turns to green;
  count of intermediate states that did not compile; total tokens.

Recording tokens too keeps the result honest in both directions: a quality win bought with a large
token regression is a trade-off to state, not a victory to announce.

### Results

*(empty — the experiment has not been run)*

## Reproducing

Drive two `claude -p` sessions over the same prompt and a fresh clone, toggling the MCP with
`--mcp-config`/`--strict-mcp-config`, capture `--output-format json` usage, and score with
`dotnet test`:

```bash
# control: vanilla, no MCP
claude -p "<task>" --output-format json --permission-mode bypassPermissions \
  --mcp-config empty.json --strict-mcp-config

# treatment: RoselineMCP available (add --disallowedTools Read Grep Glob to force its use)
claude -p "<task>" --output-format json --permission-mode bypassPermissions \
  --mcp-config roseline.mcp.json --strict-mcp-config
```

Compare `usage` (input/output/cache tokens, `total_cost_usd`) between the two, and confirm both
produce equal quality (build + tests). Single runs are noisy — repeat the run you intend to cite.
