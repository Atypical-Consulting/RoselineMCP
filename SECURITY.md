# Security Policy

## Supported Versions

RoselineMCP is currently pre-1.0/early-stage software distributed as a NuGet
global tool. Security fixes are applied to the latest published release on
the `dev` branch. There is no long-term support (LTS) branch at this time.

| Version | Supported          |
| ------- | ------------------ |
| Latest  | :white_check_mark: |
| Older   | :x:                |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub
issues, discussions, or pull requests.**

Instead, report vulnerabilities privately using GitHub's private
vulnerability reporting feature:

1. Go to the [Security tab](https://github.com/Atypical-Consulting/RoselineMCP/security) of this repository.
2. Click **"Report a vulnerability"**.
3. Fill in as much detail as you can, including:
   - A description of the vulnerability and its potential impact
   - Steps to reproduce (a minimal solution/project that triggers the issue is ideal)
   - The RoselineMCP version, .NET SDK version, and OS you tested on
   - Any relevant MCP tool call and parameters (e.g. `AnalyzeSolution`, `ApplyFixes`) involved

If you are unable to use GitHub's private reporting for any reason, you may
open a regular issue asking a maintainer to provide an alternative private
contact channel — please do not include vulnerability details in that issue.

### Response Time

We aim to acknowledge new reports within **7 days** and to provide an
initial assessment (severity, affected versions, and a remediation plan or
timeline) within that same window. Timelines for a fix or patch release will
depend on severity and complexity, and we will keep the reporter updated
throughout the process.

## Known Risk: MSBuild Design-Time Evaluation

RoselineMCP loads solutions and projects via `MSBuildWorkspace` in order to
run Roslyn analyzers and code fix providers. **Loading a `.csproj`/`.sln`
this way is a design-time MSBuild evaluation, not a sandboxed parse.**
MSBuild evaluation can execute build logic embedded in the project files it
loads, including but not limited to:

- `<Exec>` tasks and other inline shell/process invocations
- Custom MSBuild task assemblies (`UsingTask`) referenced by the project
- Imported `.targets`/`.props` files, including ones pulled in transitively
  via NuGet packages or `Directory.Build.props`/`Directory.Build.targets`

This means analyzing a **fully untrusted** repository (e.g. an arbitrary
Git URL supplied to `AnalyzeSolution`) carries a real risk of arbitrary code
execution on the host running RoselineMCP, independent of the "no code
execution" guarantees that apply to the *analyzed application code itself*.
RoselineMCP does not attempt to sandbox or disable MSBuild task execution
during workspace loading.

**Analyzer execution is a second, related surface.** The diagnostics tools
(`AnalyzeSolution`, `ListDiagnostics`, `ApplyFixes`) run Roslyn analyzers by
default: the Roslynator analyzers bundled with RoselineMCP *and* whatever
analyzer assemblies the target project itself references. A referenced
analyzer is arbitrary .NET code executed in-process at analysis time — an
untrusted repository can therefore run code through its analyzer references
even before any build target fires. Setting `RoselineMCP:RunAnalyzers` to
`false` disables the **diagnostic analyzer** pass (bundled and
project-referenced alike), reducing the diagnostics tools to compiler-only
diagnostics; MSBuild evaluation itself (above) still applies.

**`RunAnalyzers=false` does not stop source generators.** Generators are
shipped through the same `AnalyzerReferences` as analyzers and are equally
arbitrary in-process .NET code, but they run as part of *building a
compilation* rather than as part of the diagnostics pass — so the switch does
not reach them. Any tool that needs semantic information builds a compilation,
which means generators from the target repository execute on:

- **every navigation tool** (`SearchSymbols`, `GetSymbolInfo`, `FindReferences`,
  `FindImplementations`, `GetCallGraph`, `GetTypeHierarchy`,
  `GetSymbolAtPosition`), via `SymbolResolver`;
- `ApplyFixes`, via `CodeFixService`;
- `AnalyzeSolution`, via `SolutionAnalyzerService`.

Suppressing them is not offered, because it would not be honest: stripping a
project's `AnalyzerReferences` before compiling removes the generated types
along with the generators, and every symbol that resolves through generated
code would then be reported as a compile error. Semantic analysis of a modern
.NET project requires running its generators.

`RunAnalyzers=false` therefore **narrows** the code-execution surface of an
untrusted repository; it does not close it. MSBuild evaluation and source
generators both remain. Isolation — not the switch — is the mitigation.

**The write-confirmation gate is operator-disablable.** The three write tools
(`ApplyFixes`, `EditMember`, `RenameSymbol`) write nothing unless the caller
passes `previewOnly: false`; behind that opt-in they ask the connected client
to confirm via MCP elicitation. That confirmation is already best-effort — it
is skipped when no server is attached, when the client does not support
elicitation, or when the round-trip fails — and setting
`RoselineMCP:ConfirmDestructiveWrites` to `false` skips it unconditionally, so
no elicitation is sent at all. It exists for unattended hosts (CI, headless
agents) whose client *can* elicit but has no human to answer, where the prompt
would otherwise stall the call rather than guarding it. Turning it off leaves
the explicit `previewOnly: false` opt-in as the only thing between a tool call
and a disk write — so leave it enabled (the default) on any interactive
install, and treat disabling it as a decision about a specific deployment.

**The confirmation names the target it is about to write.** The prompt carries
the concrete `.sln`/`.csproj` path — resolved by the same function, against the
same base directory, that the loader uses — rather than a placeholder, and it
does so whether the caller passed `project`, omitted it, or passed an empty
string. The resolved path is then what the write is performed against, so the
answer cannot be given about one target and spent on another: resolving a second
time after a round-trip that may last minutes would reopen exactly that window. This matters because `project` is
optional and auto-discovery walks the working directory, up to three parent
directories and then the immediate subdirectories: a server launched from an
unexpected directory can present a write to a solution the human never had in
mind, and the path in the prompt is the only thing that lets them notice. A
target that cannot be resolved at all is a failure, not a question — the call
returns its error envelope without eliciting, since asking someone to approve a
write that cannot be targeted spends their attention on a call that was going
to fail regardless.

**The caller cannot author the sentence a human approves.** Two of the values a
prompt names — `symbol` and `newName` — are free-form caller input, and until
v2.2.0 they were interpolated raw. A symbol carrying quote-and-punctuation
therefore rendered a *complete, plausible, benign-looking* sentence that ended
before the real one began: a human could read that first sentence, see a scratch
project, approve — and the write would land on the resolved target instead. The
gate exists to let a human refuse, so a sentence the guarded party can partly
write is not a gate. Every caller-supplied value is now escaped and length-capped
in the one place the sentence is composed (`WritePrompt.Render`): whitespace is
removed, an apostrophe becomes `’` so it cannot open or close a quoted run, and
an over-long value is elided in the middle. A C# symbol reference contains
neither whitespace nor an apostrophe, so an ordinary name is unaffected.

What this does and does not guarantee. It guarantees the *shape*: one question,
and every quoted run opened and closed by the fixed frame, so the last quoted run
is always the real target. It does **not** guarantee that the caller-supplied
text inside those quotes is meaningful — a caller can still put misleading
characters in a symbol name, and a human should read the **target** rather than
the symbol as the load-bearing part of the sentence. The resolved target is
deliberately not escaped or elided: it must stay checkable against the file
system, and a checkout path may legitimately contain an apostrophe
(`C:\Users\O'Brien\src`). What protects it is position — every prompt puts the
target in its **last** quoted run, so no frame text follows it to be forged past.
And in every case the write is performed against the resolved target, never
against the string that was displayed: the displayed path is a rendering of the
write target, not its source.

**Naming the target is not the same as naming the scope**, and for `ApplyFixes`
the two differ: it is a project-scoped tool whose resolved target may be a
solution, so when the prompt names a `.sln` it says *the primary project of* it
— the single project the fixes actually land in. Approving that prompt does not
authorise a solution-wide rewrite, and the projects that are skipped are not
reported anywhere in the response. `EditMember` is narrower still, and its prompt
says so outright — *exactly one file is rewritten* — rather than letting the
target stand in for the scope. Note what that sentence deliberately does **not**
claim. It does not say the file is *in* the named target: a `.csproj` does not
bound the write, because `ProjectLoader` opens the containing solution and symbol
resolution spans every project in it, so the file rewritten can belong to a
sibling project the caller never named. And it does not claim to be *the* file
declaring the symbol: a partial type has several declarations and Roslyn picks
one. Which file it lands on is deliberately left unnamed, since resolving it
means loading an MSBuild workspace before the human has agreed to anything — so
approving an `EditMember` prompt authorises *one* write, not a known one.
`RenameSymbol` carries no scope qualifier, and there none is needed: the rename
really can rewrite files across every project in the solution.

**An unanswered confirmation declines, it does not proceed.** A client that
accepts the elicitation request and then never answers used to block the tool
call indefinitely: `RoselineMCP:DefaultTimeout` is an analysis budget and does
not apply to the human round-trip by construction, so nothing bounded it. The
wait is now capped by `RoselineMCP:ConfirmDestructiveWritesTimeout` (default
`300000` ms, 5 minutes), and expiry downgrades the call to a preview with a
note — it never writes. That direction is deliberate. The gate's other
best-effort branches assume consent because the client *cannot* be asked,
which is a capability fact knowable up front; a client that was asked and said
nothing is a different state, and reading it as approval is exactly the
inference the gate exists to prevent — an interactive user who steps away
would return to a solution-wide rename already on disk. The timeout therefore
frees the **server** while keeping the security posture no weaker than before:
writing without a human remains an explicit operator decision, spelled
`ConfirmDestructiveWrites=false`. Setting the timeout to `0` or less restores
the unbounded wait for a deployment that genuinely wants it.

⚠️ **It frees the server, which is not always the same as freeing the caller.**
The SDK's client dispatches server-initiated requests on its read loop, so a
client whose elicitation handler never returns also stops reading responses —
including the preview this server has already written and moved on from. In
that configuration the server no longer holds the call, but the caller may
still not observe the result until its own handler returns or its own timeout
fires. The bound is on RoselineMCP's side of the wire; it cannot unblock a
client that has blocked itself.

**Recommendations for operators:**

- **This is the primary mitigation, not a fallback:** only point RoselineMCP at
  repositories and branches you trust, or run it in an isolated/ephemeral
  environment (container, VM, CI sandbox) when analyzing third-party code. No
  configuration switch substitutes for it — `RunAnalyzers=false` narrows the
  surface but leaves MSBuild evaluation and source generators running.
- Review project files before analysis when working with untrusted input —
  including their `AnalyzerReferences`, which carry both analyzers and
  generators.
- Treat the `pathOrGit`/`branch` parameters of `AnalyzeSolution` as a code
  execution surface, not just a data source, when reasoning about threat
  models.
- Leave `RoselineMCP:ConfirmDestructiveWrites` at its default (`true`) on any
  install a human actually sits in front of. Disable it only for a specific
  unattended deployment, and treat that deployment as one where any
  `previewOnly: false` call writes unreviewed. The server logs a warning at
  startup when the switch is off.
- On an unattended host, disable the gate rather than relying on the timeout to
  get you through it. Waiting out `ConfirmDestructiveWritesTimeout` returns a
  preview, not a write, so a CI job that needs `previewOnly: false` to take
  effect must set `ConfirmDestructiveWrites=false` — the timeout ends the
  server-side wait, it does not grant consent, and it cannot unblock a client
  that is itself parked on an unanswered prompt.
- Keep `RoselineMCP:ConfirmDestructiveWritesTimeout` above `0` on any install
  reachable by an automated caller. `0` restores the unbounded wait, in
  which a client that never answers pins the call — and the slot it holds —
  indefinitely, with no error to diagnose it by. The one signal is a startup
  warning on stderr: the server logs that the confirmation is unbounded
  whenever the gate is enabled and the timeout is `0` or less. Check stderr
  before concluding a wedged call is unexplained.

If you find a way to escalate this into a more severe issue (e.g. bypassing
intended read-only guarantees for the *output* of analysis, or path
traversal outside the designated workspace), please report it as described
above.

## Compile Verification and the Write Gate

Since 3.0.0 the three write tools (`apply_fixes`, `edit_member`, `rename_symbol`) compile the
candidate change **in memory** before anything reaches disk, and refuse it when it introduces
compiler errors. Two things follow that matter for a threat model.

**What it guarantees, stated narrowly.** *The verified change set compiles, and no refused edit is
ever written.* It is **not** "the working tree always compiles after any outcome". Both multi-file
writers apply changes file by file, so a cancellation or I/O failure partway through a twelve-file
rename leaves some files written and some not — a state neither the baseline nor the candidate
describes. That boundary is covered by a test
(`CodeEditServiceTests.RenameSymbol_Multi_File_Write_Is_Not_Atomic`) rather than assumed away, and
it is not a defence you should build on.

**It is a correctness gate, not a security boundary.** `allowIntroducedErrors: true` waives it from
the tool call, so it constrains mistakes, not an adversarial caller. The guards that constrain a
caller are the explicit `previewOnly: false` opt-in and the write-confirmation elicitation, both
unchanged. `scopeComplete: false` in the verdict means the gate could not prove it saw every
dependent (a bare `.csproj` with no containing solution) — the write still proceeds, and the caller
is told the check was partial rather than handed a false green.

**`check_compilation` carries the same execution surface as every other semantic path.** It builds a
`Compilation`, so **source generators** referenced by the target project run, exactly as they do for
the navigation tools, `apply_fixes` and `analyze_solution` (see *Analyzer execution is code
execution* above). It does **not** run diagnostic analyzers — it is compiler-only — so it is a
strictly *narrower* surface than `list_diagnostics`, not a new one.

## Known Risk: Git Clone SSRF Mitigation Is Not TOCTOU-Proof

When `AnalyzeSolution` is given an `http(s)://` `pathOrGit` URL, RoselineMCP
resolves the URL's host via DNS and rejects the request up front if any
resolved address is loopback, link-local (including the
`169.254.169.254` cloud metadata address), or in a private (RFC1918) range.
This blocks straightforward SSRF attempts against internal services.

This check is performed once, before the clone starts, and is **not**
immune to DNS-rebinding: the system `git` executable performs its own DNS
resolution when it actually connects, which happens moments later and can
return a different (internal) address than the one validated. Closing that
gap fully would require routing the clone through a connection pinned to
the validated address rather than shelling out to `git`, which is a larger
networking change than this mitigation covers. Operators analyzing
untrusted Git URLs should still apply the same isolation recommended above
for the MSBuild execution risk.
