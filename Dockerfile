# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The runtime stage below is alpine (musl) and docker-publish.yml builds linux/amd64 +
# linux/arm64, so map BuildKit's TARGETARCH to the matching musl RID. The RID is passed on
# the command line only - RoselineMCP.csproj intentionally sets no RuntimeIdentifier(s)
# (it would break the PackAsTool NuGet package; see the comment there).
ARG TARGETARCH
RUN arch="${TARGETARCH:-$(uname -m)}" \
    && case "$arch" in \
         amd64|x86_64)  rid=linux-musl-x64 ;; \
         arm64|aarch64) rid=linux-musl-arm64 ;; \
         *) echo "Unsupported architecture: $arch" >&2; exit 1 ;; \
       esac \
    && echo "$rid" > /tmp/rid

# Copy solution and project files for layer caching
COPY RoselineMCP.sln ./
COPY RoselineMCP/RoselineMCP.csproj RoselineMCP/
COPY RoselineMCP.Tests/RoselineMCP.Tests.csproj RoselineMCP.Tests/
COPY RoselineMCP.Benchmarks/RoselineMCP.Benchmarks.csproj RoselineMCP.Benchmarks/

# Restore dependencies. PublishReadyToRun must also be set here so the crossgen2
# compiler pack is acquired for the --no-restore publish below.
RUN dotnet restore -r "$(cat /tmp/rid)" -p:PublishReadyToRun=true

# Copy source
COPY . .

# Publish framework-dependent but RID-specific, with ReadyToRun (AOT-precompiled native
# code alongside the IL) to cut JIT work on cold start - measured first-call latency was
# ~1456 ms cold vs ~590 ms warm, and most of that gap is JIT/assembly load.
RUN dotnet publish RoselineMCP/RoselineMCP.csproj \
    -c Release \
    --no-restore \
    -r "$(cat /tmp/rid)" \
    --self-contained false \
    -p:PublishReadyToRun=true \
    -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app

# Install MSBuild dependencies (required by Roslyn workspace) and git (required for
# cloning remote repositories when analyzing solutions via a git URL)
RUN apk add --no-cache icu-libs git

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/publish .

# Run as an unprivileged user rather than root
RUN addgroup -S roseline \
    && adduser -S roseline -G roseline \
    && chown -R roseline:roseline /app
USER roseline

ENTRYPOINT ["dotnet", "RoselineMCP.dll"]
