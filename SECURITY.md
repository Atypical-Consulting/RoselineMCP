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
`false` disables all analyzer execution (bundled and project-referenced
alike), reducing the tools to compiler-only diagnostics; MSBuild evaluation
itself (above) still applies.

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
removes the hang while keeping the security posture no weaker than before:
writing without a human remains an explicit operator decision, spelled
`ConfirmDestructiveWrites=false`. Setting the timeout to `0` or less restores
the unbounded wait for a deployment that genuinely wants it.

**Recommendations for operators:**

- Only point RoselineMCP at repositories and branches you trust, or run it
  in an isolated/ephemeral environment (container, VM, CI sandbox) when
  analyzing third-party code.
- Review project files before analysis when working with untrusted input.
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
  effect must set `ConfirmDestructiveWrites=false` — the timeout ends the hang,
  it does not grant consent.
- Keep `RoselineMCP:ConfirmDestructiveWritesTimeout` above `0` on any install
  reachable by an automated caller. `0` restores the unbounded wait, in
  which a client that never answers pins the call — and the slot it holds —
  indefinitely, with no error and no log to diagnose it by.

If you find a way to escalate this into a more severe issue (e.g. bypassing
intended read-only guarantees for the *output* of analysis, or path
traversal outside the designated workspace), please report it as described
above.

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
