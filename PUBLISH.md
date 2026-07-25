# Publishing RoselineMCP

RoselineMCP is published automatically by two GitHub Actions workflows. There is no manual
publish step for maintainers — pushing a version tag is the only thing required.

## How it works

Both workflows trigger on the same event:

```yaml
on:
  push:
    tags:
      - 'v*'
```

Pushing a tag matching `v*` (e.g. `v1.2.0`) to the repository triggers **both** workflows in
parallel:

| Workflow | File | What it publishes |
|----------|------|--------------------|
| **Publish NuGet** | [`.github/workflows/publish-nuget.yml`](.github/workflows/publish-nuget.yml) | Packs `RoselineMCP/RoselineMCP.csproj` with `-p:MinVerVersionOverride=<tag-without-v>` and pushes the `.nupkg` to [nuget.org](https://www.nuget.org/packages/RoselineMCP/) via Trusted Publishing — the GitHub OIDC token is exchanged for a key valid ~1 hour, so no long-lived API key exists. |
| **Docker Publish** | [`.github/workflows/docker-publish.yml`](.github/workflows/docker-publish.yml) | Builds a multi-arch (`linux/amd64`, `linux/arm64`) image from the repository [`Dockerfile`](Dockerfile) via Docker Buildx and pushes it to both `docker.io/phmatray/roseline-mcp` and `ghcr.io/atypical-consulting/roseline-mcp`, tagged with the semver version and `latest`. |

### Releasing a new version

1. Make sure `dev` (or the release branch) is green on CI (`.github/workflows/ci.yml`).
2. Update `CHANGELOG.md` — move the `[Unreleased]` entries under a new `[x.y.z] - YYYY-MM-DD`
   heading.
3. Merge to the branch the release is cut from, then create and push an annotated tag:

   ```bash
   git tag -a v1.2.0 -m "v1.2.0"
   git push origin v1.2.0
   ```

4. Watch the **Publish NuGet** and **Docker Publish** workflow runs in the
   [Actions tab](https://github.com/Atypical-Consulting/RoselineMCP/actions).

The package version is derived entirely from the pushed tag (`GITHUB_REF_NAME` with the leading
`v` stripped); there is nothing to bump in the `.csproj` beforehand — versioning also flows
through [MinVer](https://github.com/adamralph/minver) for local/CI builds between tags.

## Required secrets

| Secret | Used by | Purpose |
|--------|---------|---------|
| `NUGET_USER` | `publish-nuget.yml` | The nuget.org profile name — not a credential. Push rights come from a [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) policy registered on nuget.org for the `RoselineMCP` package, naming this repository and `publish-nuget.yml`. No long-lived API key is stored. |
| `DOCKER_USERNAME` / `DOCKER_TOKEN` | `docker-publish.yml` | Docker Hub login (access token, not a password) for `phmatray/roseline-mcp`. |
| `GITHUB_TOKEN` | `docker-publish.yml` | Automatically provided by Actions; used to push to `ghcr.io/atypical-consulting/roseline-mcp`. |

## Testing a build locally without publishing

You don't need to push a tag to verify a container builds correctly:

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
  multi-arch manifests, etc.) is intentionally not documented here — always publish through a
  tagged push so both the NuGet package and the Docker image stay in lockstep with the same
  version and the same CI-verified commit.
- `publish-docker.sh` in the repository root predates the `docker-publish.yml` workflow above and
  is not used by CI; prefer the tag-push flow described here.
