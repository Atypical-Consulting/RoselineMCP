# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files for layer caching
COPY RoselineMCP.sln ./
COPY RoselineMCP/RoselineMCP.csproj RoselineMCP/
COPY RoselineMCP.Tests/RoselineMCP.Tests.csproj RoselineMCP.Tests/

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
FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine AS runtime
WORKDIR /app

# Install MSBuild dependencies (required by Roslyn workspace)
RUN apk add --no-cache icu-libs

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "RoselineMCP.dll"]
