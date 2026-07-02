# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files for layer caching
COPY RoselineMCP.sln ./
COPY RoselineMCP/RoselineMCP.csproj RoselineMCP/
COPY RoselineMCP.Tests/RoselineMCP.Tests.csproj RoselineMCP.Tests/
COPY RoselineMCP.Benchmarks/RoselineMCP.Benchmarks.csproj RoselineMCP.Benchmarks/

# Restore dependencies
RUN dotnet restore

# Copy source
COPY . .

# Publish
RUN dotnet publish RoselineMCP/RoselineMCP.csproj \
    -c Release \
    --no-restore \
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
