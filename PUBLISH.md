# Publishing RoselineMCP

RoselineMCP is released by [release-please](https://github.com/googleapis/release-please). There is
no tag to push and no version to bump by hand — **merging the release PR is the whole release**.

## How it works

One workflow does everything, triggered by ordinary pushes to `dev`:

```yaml
on:
  push:
    branches: [dev]
```

[`.github/workflows/release-please.yml`](.github/workflows/release-please.yml) runs on every push to
`dev` and keeps a **release PR** up to date, derived from the [Conventional
Commits](https://www.conventionalcommits.org/) since the last release. That PR contains the version
bump, the regenerated `CHANGELOG.md`, and the three JSON manifest version fields. Merging it tags
`vX.Y.Z`, creates the GitHub Release, and — in the **same run** — publishes everything:

| Job | Gated on | What it does |
|-----|----------|--------------|
| `release-please` | every push to `dev` | Maintains the release PR. On merge: creates the tag and the GitHub Release, and sets `release_created`. |
| `publish` | `release_created` | Packs `RoselineMCP/RoselineMCP.csproj`, asserts the packed `.mcp/server.json` matches the release version, pushes the `.nupkg` to [nuget.org](https://www.nuget.org/packages/RoselineMCP/) via Trusted Publishing (OIDC — no long-lived key), builds `RoselineMCP.mcpb`, attaches both to the Release, then triggers the docs rebuild. |
| `publish-registry` | `publish` | Waits for NuGet to index the version, then publishes `.mcp/server.json` to [registry.modelcontextprotocol.io](https://registry.modelcontextprotocol.io). |
| `docker` | `publish` | Builds a multi-arch (`linux/amd64`, `linux/arm64`) image from the [`Dockerfile`](Dockerfile) and pushes it to `docker.io/phmatray/roseline-mcp` and `ghcr.io/atypical-consulting/roseline-mcp`, tagged with the version and `latest`. |

### Why publishing lives in the release workflow

release-please creates the tag with `GITHUB_TOKEN`, and **GitHub deliberately does not fire
`on: push: tags` or `on: release` for events created by that token.** A tag-triggered publish
workflow would therefore never run again — silently, with no failed run to notice. That is why the
former `publish-nuget.yml` and `docker-publish.yml` were folded into this one workflow and gated on
`release_created` instead.

The same rule is why the docs rebuild is triggered with `gh workflow run deploy-docs.yml`:
`workflow_dispatch` and `repository_dispatch` are the documented exceptions, so a dispatch fires
where a `release:` trigger would not.

### Releasing a new version

1. Land your work on `dev` with **Conventional Commit** PR titles (`feat:`, `fix:`, `docs:`, …) —
   the repo squash-merges, so the PR title becomes the commit release-please parses. The type
   selects both the version bump and the changelog section.
2. release-please opens (or updates) a release PR titled `chore(main): release X.Y.Z`. Review it —
   the version it chose and the generated `CHANGELOG.md` entries. **Generated entries are one-line
   commit subjects; expand any that lose something that mattered.** It is an ordinary PR.
3. Merge it. That is the release: the same run tags, creates the Release, and publishes to NuGet,
   the MCP Registry, Docker Hub and GHCR.
4. Watch the **release-please** run in the
   [Actions tab](https://github.com/Atypical-Consulting/RoselineMCP/actions).

The package version is derived from the tag release-please creates, via
[MinVer](https://github.com/adamralph/minver) — nothing in the repository records a `<Version>`
element, and `release-please-config.json` explains why.

> **If a publish job fails**, the tag and the GitHub Release already exist. Recover with **"Re-run
> failed jobs"** — *not* "Re-run all jobs", which re-runs `release-please`, which then sees the
> release already created, reports `release_created: false`, and skips every publishing job. The
> pushes are idempotent (`--skip-duplicate`, `--clobber`), so re-running is safe.

## Required secrets

| Secret | Used by | Purpose |
|--------|---------|---------|
| `NUGET_USER` | `release-please.yml` (`publish`) | The nuget.org profile name — not a credential. Push rights come from a [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) policy registered on nuget.org for the `RoselineMCP` package, naming this repository and **`release-please.yml`**. No long-lived API key is stored. |
| `DOCKER_USERNAME` / `DOCKER_TOKEN` | `release-please.yml` (`docker`) | Docker Hub login (access token, not a password) for `phmatray/roseline-mcp`. |
| `GITHUB_TOKEN` | `release-please.yml` | Automatically provided by Actions; used for the release PR, the tag, the Release, and the GHCR push. |

> ⚠️ **The Trusted Publishing policy names the workflow *file*.** It named `publish-nuget.yml`
> until that workflow was replaced, so the policy must name `release-please.yml` **before the first
> release**. If it does not, the release still tags and still creates a GitHub Release, and then
> **403s on push** — leaving a published Release with no package behind it.

## Testing a build locally without publishing

You don't need a release to verify a container builds correctly:

```bash
docker build -t roseline-mcp:local .
docker run --rm -i roseline-mcp:local
```

Or to verify the NuGet package packs cleanly:

```bash
dotnet pack RoselineMCP/RoselineMCP.csproj -c Release -p:MinVerVersionOverride=0.0.0-local -o ./nupkg
```

Neither of these pushes anything anywhere — they're safe to run at any time.

## Notes

- Manual, ad hoc publishing (`dotnet nuget push`, `docker buildx build --push`, hand-built
  multi-arch manifests, etc.) is intentionally not documented here — always publish by merging the
  release PR, so the NuGet package, the MCP Registry entry and the Docker image stay in lockstep
  with the same version and the same CI-verified commit.
- `publish-docker.sh` in the repository root predates the release workflow and is not used by CI;
  prefer the release-PR flow described here.
- A push to `dev` that contains only hidden commit types (`chore`, `style`, `test`) produces no
  release PR — there is nothing releasable to describe. That is expected, not a failure.
