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

> **What this document does *not* show: that the compile-verified edit loop improves quality.** Every
> table above measures cost. The one experiment aimed at correctness
> ([the quality A/B](#does-the-compile-gate-change-quality-pre-registered-run-once-inconclusive),
> six sessions) was **inconclusive**: neither pre-registered axis improved, and the treatment arm's
> gate never fired, so it neither supports nor refutes the claim. Do not cite the token figures as
> evidence of a correctness benefit.

## Method

Each cell is one real Claude Code session (`claude -p`, Claude Sonnet), run on a **fresh git clone**
of the target repo, differing only in which tools are available:

- **Control** — vanilla Claude Code (`Read`/`Grep`/`Glob`), **no MCP servers**.
- **+MCP** — the same, plus RoselineMCP. In the *comprehension* rows the agent was **forced** to
  navigate through RoselineMCP (`Read`/`Grep`/`Glob` removed) so the tools are actually exercised;
  in the *greenfield/brownfield* rows the MCP was merely **available** (the realistic default).

Quality is an objective gate — `dotnet test` / injected acceptance tests / a graded structural
answer — not a subjective judgement. "Tokens" is total input tokens (regular + cache) from the
session's reported usage. Two further quantities are defined here because they are easy to
conflate — they are related but **not interchangeable**:

- **Turns** — assistant turns in the session: the agent-loop iterations (one model response each).
  This is the quantity per-session cost grows with, because every turn re-reads the context that
  carried over from the previous one.
- **Tool calls** — individual tool invocations. Several can occur in a single turn, so a session
  has at least as many turns as it has *sequential* round trips, but its tool-call count can exceed
  its turn count.

**n = 1 per cell**: treat single-digit-percent gaps as noise; the 2×
gap on the large repo is not noise.

## Results

Quality was **identical in every cell** — every run produced passing tests / the correct answer.
So RoselineMCP was never a *correctness* factor here; the only variable that moved is token cost.

| Scenario | Target files | Control (tokens / $) | +MCP (tokens / $) | Tool calls (MCP) | Turns | Token Δ |
|---|---|---|---|---|---|---|
| **Greenfield** — build a small library from a spec | brand new | 453,554 / $0.42 | 440,268 / $0.39 *(available)* | **0** | — | −3% (noise) |
| **Brownfield** — add a feature to a *small* solution | ~30-line | 691,499 / $0.61 | 457,918 / $0.46 *(available)* | **0** | — | −34%, but see below |
| **Comprehension** — map a *small* solution | ~30-line | 453,722 / $0.46 | 350,189 / $0.64 *(forced)* | **11** | — | −23% tok / +37% $ |
| **Comprehension** — map a *large* solution (RoselineMCP) | 700-line | 499,675 / $0.50 | **239,357 / $0.37** *(forced)* | **3** | — | **−52% tok / −27% $** |

*Turns (—): not recorded.* These four sessions are gone and their turn counts were never kept, so
the cell is empty rather than reconstructed. That gap is why the column now exists; see
[Why turns matter](#why-turns-matter-and-where-turn-control-does-not-belong) and
[Reproducing](#reproducing). A tool-call count of `0` still answers "was the MCP used at all?".

## What it means

1. **The benefit scales with file size — that is the whole story.** On large source files
   (RoselineMCP's own ~700-line services) navigating structurally instead of reading whole files
   **roughly halved** the tokens for the same correct answer, using 3 tool calls in total (the
   control's turn count was not recorded, so "fewer turns" is an unrecorded observation, not a
   measurement) — under forced use, i.e. the ceiling. This is where the unit benchmark's per-call savings
   (85% median) convert into real end-to-end savings. On tiny files it is
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

## Why turns matter, and where turn control does not belong

Cost is not only a function of how big each tool response is. *"More with Less: An Empirical Study
of Turn-Control Strategies for Efficient Coding Agents"* (Gao & Peng, ICSE 2026,
[arXiv:2510.16786](https://arxiv.org/abs/2510.16786)) reports, on SWE-bench across three frontier
models — **these are that paper's findings, not RoselineMCP measurements**:

- agent cost grows **quadratically with the number of turns**, because context carries over from
  turn to turn, and controlling the *total* number of turns is under-explored;
- a **fixed** turn cap at the 75th percentile of the unconstrained baseline cuts cost by
  **24%–68%** with minimal impact on resolution rate;
- a **dynamic** strategy (extensions granted on request) does a further **12%–24%** better than the
  fixed cap.

One hint inside this document is worth testing, as a **hypothesis at n = 1, not a finding**: the
11-call comprehension cell is the only `+MCP` cell where dollars rose (+37%) while tokens fell
(−23%), which is the shape a spend-follows-round-trips model predicts — but the 11-call and 3-call
cells are different repos and different tasks, so they are confounded, and one run per cell cannot
separate a structural effect from a lucky run.

**Scope boundary: RoselineMCP will not implement turn control.** The server is called by the
client's loop; it does not call, cannot see the loop, and cannot cap, budget or extend it. The
paper's better (dynamic) strategy needs the agent to be able to request an extension, which
requires sitting inside the loop. The lever RoselineMCP *does* own is the tool surface: it decides
how many round trips a unit of understanding costs. The repo has pulled it once — making `project`
optional took the same task from **8 calls / 594k tokens to 3 calls / 437k tokens** (see the
[follow-up table](#follow-up--making-the-model-actually-use-the-tools)).

## Follow-up — making the model actually use the tools

The finding above (the model won't call the MCP by default) turned out to be fixable in-product.
Three levers, each tested on the large-repo comprehension task in **plain mode** — the MCP available,
**no external nudge**, so it reflects real product behavior:

| Build | roseline tool calls (unprompted) | failed on `project` | Read | Tokens |
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

## Does the compile gate change *quality*? (pre-registered; run once, inconclusive)

Everything above measures cost. The line that matters most in it is this one: **quality was
identical in every cell.** RoselineMCP has never been a correctness factor, only a cost one — which
is a ceiling, not a plateau, and it is the ceiling the compile-verified edit loop (#133) exists to
break.

This section is written **before the experiment runs**, so the criterion cannot be chosen after
seeing the data. It records what would count as the bet paying off, and what would count as it
failing. The criterion, task and `n` below are exactly as written before the run; the
[Results](#results) section records what happened, including that the treatment arm did not
exercise the mechanism.

> **Status: run once (6 sessions, 2026-10-09), inconclusive.** Neither axis improved, but the treatment
> arm's guard fired **0 times in 3 runs**, so this measures nothing about the gate. Read
> [Results](#results) before citing it — in either direction.

> **Update (compile guard, #168) — this changes what the treatment arm *is*, not what counts as
> success.** As written below, the treatment assumes the agent routes its writes through
> RoselineMCP's write tools, since that is the only place the gate used to fire. This document's own
> central finding says that assumption does not hold: *"An MCP that isn't invoked delivers nothing"*
> — with the server merely available the agent made **0** RoselineMCP calls and used `Read`. Write-tool
> adoption has never been measured, but it runs on the same mechanism. So the treatment arm risked
> measuring a gate that mostly never fired.
>
> The compile guard (`RoselineMCP:Guard=true`, plus the `PostToolUse` hook) applies the verdict after
> **every** file write regardless of which tool made it, which is what lets the treatment arm be run
> in realistic mode rather than a forced one. Two consequences for whoever runs this:
>
> - **Enable the guard in the treatment arm and leave it off in the control.** That keeps the single
>   variable "does the agent get told", which is what the criterion above is about.
> - **The guard reports, it does not block.** `PostToolUse` carries no blocking decision, so the
>   mechanism under test is feedback-in-the-same-turn, not prevention. The named risk below — that a
>   gate can *raise* turns-to-green by interrupting a state the agent would have repaired on its own —
>   applies unchanged, and arguably more sharply, since the guard fires more often than the write gate
>   ever did.
>
> **The falsification criterion above is untouched, deliberately.** It was written before the data
> and it stays that way; nothing here relaxes it, and nothing here is a result.

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

Six `claude -p` sessions (Claude Sonnet, `claude-sonnet-5-5`), run 2026-10-09, one per row, **none
re-run, none dropped**. Build under test: a `dev`-based worktree build carrying #145 and #168
(`3.1.2-alpha.0.10`). Fixture and harness are committed under
[`RoselineMCP.Benchmarks/Fixtures/QualityAb/`](../RoselineMCP.Benchmarks/Fixtures/QualityAb)
(`make-fixture.sh`, `prompt.txt`, `run-cell.sh`, `snap.sh`, `prime.sh`, `analyze.py`).

| Run | Final tree compiles | Broken intermediate states | Turns to green | Turns | Tool calls | Tokens | $ |
|---|---|--:|--:|--:|---|--:|--:|
| control 1 | yes | 0 | 0 | 6 | Bash 4, find_references 1, ToolSearch 1 | 124,366 | 0.0696 |
| control 2 | yes | 1 | 0 | 5 | Bash 4, find_references 1, edit_member 1, ToolSearch 1 | 103,872 | 0.0670 |
| control 3 | yes | 1 | 0 | 5 | Bash 4, find_references 1, edit_member 1, ToolSearch 1 | 104,621 | 0.0702 |
| treatment 1 | yes | 0 | 0 | 5 | Bash 4 | 94,389 | 0.0498 |
| treatment 2 | yes | 1 | 1 | 5 | Bash 4 | 93,713 | 0.0479 |
| treatment 3 | yes | 0 | 0 | 5 | Bash 4 | 96,223 | 0.0578 |

Total cost of the six sessions: **$0.3623**. "Turns" is the distinct-assistant-message count (the
method above); tokens are input + cache-creation + cache-read + output from the result event.
"Broken intermediate states" counts distinct snapshot trees, taken after every `Edit`, `Write`,
`MultiEdit`, `Bash` or RoselineMCP call, whose `dotnet build` failed. "Turns to green" is measured
from the first such tree to the first later one that builds; `0` means broken and repaired inside one
turn (or never broken). Both are bounded by that observer: a break repaired *within* one tool call
(for instance one `sed` over three files) is invisible to it.

**Verdict against the pre-registered criterion, which was not changed.**

- **Axis 1, broken final states:** 0 of 3 versus 0 of 3. A tie at zero, so, as pre-registered, this
  axis is uninformative rather than passed.
- **Axis 2, turns to green:** treatment 0, 1, 0 versus control 0, 0, 0. Treatment is **not** lower, so
  the axis did not move in the direction the bet needs.
- Neither axis improved, so by the literal criterion the bet **did not pay off** on this data. The
  third pre-registered outcome (the gate raising turns to green) shows up as treatment 2 (1 turn
  against 0), but it cannot be attributed to the gate, see below.

**This run does not test the gate, and should not be cited as evidence about it.** The treatment arm
is only a treatment if the mechanism fires, and the sanity check the protocol calls for failed:

1. **The guard fired 0 times in 3 treatment runs.** All three agents edited with `Bash` (`sed -i`,
   `cat > file <<EOF`) and never used `Edit`, `Write` or a RoselineMCP write tool. The guard hook
   receives no `file_path` for a `Bash` call and stays silent by contract, so it never judged a single
   write. The #168 note above, that the guard applies "regardless of which tool made the write", holds
   for the file-writing tools and not for shell writes. In none of the three runs did the guard output
   reach the agent. The one intermediate break in treatment 2 (consumers updated before `Core`) was
   therefore the agent's own ordering, not something the gate caused or prevented.
2. **The control arm is not a clean control.** Waiving the gate required telling the agent to pass
   `allowIntroducedErrors: true` on RoselineMCP write calls, which was done with an
   `--append-system-prompt` line in the control arm only (the user prompt is byte-identical). That
   line also made the control agents reach for RoselineMCP (3 of 3 used `find_references`, 2 of 3
   `edit_member`) while the treatment agents used none. The two intermediate breaks in the control
   arm are `edit_member` writes with the gate waived, which is the control behaving as designed.
3. **The fixture lets the agent avoid the failure.** `OrderPricing.ComputeTotal` has three call sites,
   all visible to one `grep`. Every run found them first and fixed them in one or two shell commands,
   so there was little room for a break to be left behind. All six final trees compile, which is a
   ceiling effect, not evidence that the gate is unnecessary.

Two setup facts found while building the harness, relevant to anyone re-running it. They are product
behaviour, not part of this measurement, and are filed (#266) rather than fixed here:

- **The guard is silent on the first write it sees for a solution** (it takes its baseline from disk
  after that write), which is by design, and, separately, **a verify that is the first to read the
  baseline's file-backed documents reads the already-edited file as the "before" state**. Measured on
  an in-project `return "x";`: `introduced = 0`, `preexisting = 1`, silent, even though
  `check_compilation` reports the error. The harness therefore primes the guard (`prime.sh`: one call
  to establish the baseline, one whitespace-only touch to materialise the documents, touch undone)
  before the agent's first prompt. With the priming the guard reports the fixture's `Core` signature
  change as three `CS7036` errors in `Consumer`. Without it, treatment would have been silent even for
  an agent that used `Edit`.
- Priming is an intervention the treatment arm gets and a real user does not; it favours the gate, and
  the gate still never fired.

**What a conclusive run needs** (a new pre-registration, not an amendment of this one): a treatment
that actually routes the agent's writes through something the gate covers (the forced mode used
elsewhere in this document, or a fixture where shell edits are impractical), a control that differs
from it in the gate alone, and a task with enough call sites that leaving one behind is a realistic
failure. Until then the 3.0.0 correctness claim stays **unevidenced**: this run neither supports nor
refutes it.

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

### Capturing turns and tool calls

`claude -p --output-format json` reports a `num_turns` field in the result object, alongside
`usage` and `total_cost_usd` (checked against a trivial prompt: `"num_turns": 1`). Record it
per run:

```bash
claude -p "<task>" --output-format json ... > run.json
jq '{turns: .num_turns, cost: .total_cost_usd, usage: .usage}' run.json
```

To count **tool calls** (and cross-check turns) run with `--output-format stream-json --verbose`
and read the transcript; the method that produced the number is the `jq` below, so state it
beside the figure:

```bash
claude -p "<task>" --output-format stream-json --verbose ... > run.jsonl

# turns = distinct assistant messages (one message may be split across several lines)
jq -r 'select(.type=="assistant") | .message.id' run.jsonl | sort -u | wc -l

# tool calls, per tool; RoselineMCP calls are the mcp__roseline__* names
jq -r 'select(.type=="assistant") | .message.content[]? | select(.type=="tool_use") | .name' run.jsonl \
  | sort | uniq -c
```

On a two-`Bash`-call probe session the distinct-message count matched `num_turns` (3), and the
tool-call listing showed `2 Bash`. This makes the hand-counted `11` / `3` above mechanically
reproducible in future runs.

**What would make the metric conclusive:** `n ≥ 3` per cell, and matched tasks (same repo, same
prompt, control vs. +MCP). The current tables meet neither bar: they are `n = 1`, and the 11-call
and 3-call cells are different repos.

**Relation to #166.** Its six pre-registered `claude -p` sessions recorded these two variables
(turns, tool calls) as well; see [Results](#results).
