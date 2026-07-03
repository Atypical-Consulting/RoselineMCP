# End-to-end agent benchmark — does RoselineMCP save tokens *in practice*?

[`BENCHMARKS.md`](../BENCHMARKS.md) measures raw service latency, and the
[token-savings benchmark](../RoselineMCP.TokenBenchmark) measures a single number in isolation: the
tokens one tool call emits versus reading the corresponding file (a pooled **88%** reduction on this
repo's own source). That's a *unit* measurement. It answers "how compact is one tool response?" — not
"does an AI agent, doing a real task end to end, actually consume fewer tokens because RoselineMCP is
installed?"

This document answers the end-to-end question with a controlled A/B test, and reports the result
honestly — including where the MCP does **not** help.

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
   **roughly halved** the tokens for the same correct answer, in half the turns (3 tool calls total).
   This is where the unit benchmark's 88% converts into real end-to-end savings. On tiny files it is
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
measured ~50% end-to-end token reduction at equal quality; on small repos it is roughly break-even;
for greenfield work it is irrelevant. The highest-leverage improvement is **adoption** — making the
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
   to 3 clean calls that now beat vanilla `Read` (437k vs 500k) — all self-directed.

Even so, self-directed use (437k) doesn't reach the *forced-minimal* path (239k): fixed
tool-schema/instruction overhead plus a little extra exploration remain. The tools are a
large-codebase win; the steering is what makes the model take it.

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
