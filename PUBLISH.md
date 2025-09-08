# Publishing RoselineMCP to Docker Hub

This guide explains how to build and publish multi-architecture Docker containers for RoselineMCP using .NET SDK container support.

## Prerequisites

1. **Docker Desktop** installed and running
2. **.NET 9.0 SDK** or later
3. **Docker Hub account** (create at https://hub.docker.com)
4. **Docker Buildx** enabled (comes with Docker Desktop)

## Initial Setup

### 1. Login to Docker Hub

```bash
docker login
# Enter your Docker Hub username and password/token
```

### 2. Verify Docker Buildx

```bash
# Check if buildx is available
docker buildx version

# Create and use a new builder instance for multi-platform builds
docker buildx create --name multiarch --use
docker buildx inspect --bootstrap
```

## Publishing Methods

### Method 1: Using .NET SDK Container Support (Recommended)

The .NET SDK can build and push containers directly using the configuration in `RoselineMCP.csproj`.

#### Single Architecture Build

```bash
# For Linux x64
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch x64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerRegistry=docker.io \
  -p:ContainerImageTag=latest-amd64

# For Linux ARM64
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch arm64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerRegistry=docker.io \
  -p:ContainerImageTag=latest-arm64
```

#### Multi-Architecture Build (Sequential)

Build and push each architecture separately, then create a manifest:

```bash
# Step 1: Build and push AMD64
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch x64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerRegistry=docker.io \
  -p:ContainerImageTag=1.0.0-amd64

# Step 2: Build and push ARM64
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch arm64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerRegistry=docker.io \
  -p:ContainerImageTag=1.0.0-arm64

# Step 3: Create and push multi-arch manifest
docker manifest create phmatray/roseline-mcp:1.0.0 \
  phmatray/roseline-mcp:1.0.0-amd64 \
  phmatray/roseline-mcp:1.0.0-arm64

docker manifest create phmatray/roseline-mcp:latest \
  phmatray/roseline-mcp:1.0.0-amd64 \
  phmatray/roseline-mcp:1.0.0-arm64

# Push the manifests
docker manifest push phmatray/roseline-mcp:1.0.0
docker manifest push phmatray/roseline-mcp:latest
```

### Method 2: Using Docker Buildx with Generated Dockerfile

First, generate a Dockerfile using .NET SDK:

```bash
# Generate Dockerfile
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch x64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerGenerateLabels=true \
  -p:ContainerGenerateDockerfile=true
```

Then use Docker Buildx for multi-platform build:

```bash
# Build and push multi-architecture image in one command
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --tag phmatray/roseline-mcp:latest \
  --tag phmatray/roseline-mcp:1.0.0 \
  --push \
  .
```

### Method 3: Using a Build Script

Create a `publish-docker.sh` script for automation:

```bash
#!/bin/bash
set -e

# Configuration
REGISTRY="docker.io"
REPOSITORY="phmatray/roseline-mcp"
VERSION="1.0.0"

echo "🔐 Logging in to Docker Hub..."
docker login

echo "📦 Building AMD64 image..."
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch x64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerRegistry=$REGISTRY \
  -p:ContainerImageTag=$VERSION-amd64

echo "📦 Building ARM64 image..."
dotnet publish RoselineMCP/RoselineMCP.csproj \
  --os linux \
  --arch arm64 \
  /t:PublishContainer \
  -c Release \
  -p:ContainerRegistry=$REGISTRY \
  -p:ContainerImageTag=$VERSION-arm64

echo "🔗 Creating multi-arch manifest..."
docker manifest create $REPOSITORY:$VERSION \
  $REPOSITORY:$VERSION-amd64 \
  $REPOSITORY:$VERSION-arm64

docker manifest create $REPOSITORY:latest \
  $REPOSITORY:$VERSION-amd64 \
  $REPOSITORY:$VERSION-arm64

echo "⬆️ Pushing manifests..."
docker manifest push $REPOSITORY:$VERSION
docker manifest push $REPOSITORY:latest

echo "✅ Successfully published $REPOSITORY:$VERSION and $REPOSITORY:latest"
```

Make it executable and run:

```bash
chmod +x publish-docker.sh
./publish-docker.sh
```

## Version Tagging Strategy

### Semantic Versioning

Use semantic versioning for releases:

```bash
# Major release (breaking changes)
dotnet publish ... -p:ContainerImageTags='"2.0.0;latest"'

# Minor release (new features)
dotnet publish ... -p:ContainerImageTags='"1.1.0;latest"'

# Patch release (bug fixes)
dotnet publish ... -p:ContainerImageTags='"1.0.1;latest"'
```

### Development Builds

For development/preview builds:

```bash
# Development build with commit hash
GIT_HASH=$(git rev-parse --short HEAD)
dotnet publish ... -p:ContainerImageTag=dev-$GIT_HASH

# Nightly build
dotnet publish ... -p:ContainerImageTag=nightly-$(date +%Y%m%d)

# Preview/RC builds
dotnet publish ... -p:ContainerImageTag=1.1.0-preview.1
```

## Container Configuration Options

Additional properties you can set in the .csproj or via command line:

```xml
<PropertyGroup>
  <!-- Container base image -->
  <ContainerBaseImage>mcr.microsoft.com/dotnet/runtime:9.0-alpine</ContainerBaseImage>
  
  <!-- Container ports -->
  <ContainerPorts>8080;443</ContainerPorts>
  
  <!-- Container environment variables -->
  <ContainerEnvironmentVariables>
    ASPNETCORE_ENVIRONMENT=Production;
    ROSELINE_LOG_LEVEL=Information
  </ContainerEnvironmentVariables>
  
  <!-- Container labels -->
  <ContainerLabels>
    org.opencontainers.image.authors=phmatray;
    org.opencontainers.image.description=MCP server for C# code analysis
  </ContainerLabels>
  
  <!-- Container user -->
  <ContainerUser>app</ContainerUser>
  
  <!-- Working directory -->
  <ContainerWorkingDirectory>/app</ContainerWorkingDirectory>
</PropertyGroup>
```

Or via command line:

```bash
dotnet publish \
  -p:ContainerBaseImage=mcr.microsoft.com/dotnet/runtime:9.0-alpine \
  -p:ContainerPorts=8080 \
  -p:ContainerUser=app
```

## Testing Published Images

### Pull and Test Locally

```bash
# Test AMD64 image on x64 machine
docker pull phmatray/roseline-mcp:latest
docker run --rm -it phmatray/roseline-mcp:latest

# Test specific architecture
docker pull phmatray/roseline-mcp:latest --platform linux/arm64
docker run --rm -it --platform linux/arm64 phmatray/roseline-mcp:latest

# Test with environment variables
docker run --rm -it \
  -e ROSELINE_LOG_LEVEL=Debug \
  phmatray/roseline-mcp:latest
```

### Inspect Image Details

```bash
# Check image manifest
docker manifest inspect phmatray/roseline-mcp:latest

# Check image size and layers
docker images phmatray/roseline-mcp:latest

# Inspect image metadata
docker inspect phmatray/roseline-mcp:latest

# Check supported platforms
docker manifest inspect phmatray/roseline-mcp:latest | jq '.manifests[].platform'
```

## CI/CD Integration

### GitHub Actions Example

Create `.github/workflows/publish-docker.yml`:

```yaml
name: Publish to Docker Hub

on:
  push:
    tags:
      - 'v*'
  workflow_dispatch:

jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Set up QEMU
        uses: docker/setup-qemu-action@v3
      
      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3
      
      - name: Login to Docker Hub
        uses: docker/login-action@v3
        with:
          username: ${{ secrets.DOCKER_USERNAME }}
          password: ${{ secrets.DOCKER_TOKEN }}
      
      - name: Extract version
        id: version
        run: echo "VERSION=${GITHUB_REF#refs/tags/v}" >> $GITHUB_OUTPUT
      
      - name: Build and push AMD64
        run: |
          dotnet publish RoselineMCP/RoselineMCP.csproj \
            --os linux \
            --arch x64 \
            /t:PublishContainer \
            -c Release \
            -p:ContainerRegistry=docker.io \
            -p:ContainerImageTag=${{ steps.version.outputs.VERSION }}-amd64
      
      - name: Build and push ARM64
        run: |
          dotnet publish RoselineMCP/RoselineMCP.csproj \
            --os linux \
            --arch arm64 \
            /t:PublishContainer \
            -c Release \
            -p:ContainerRegistry=docker.io \
            -p:ContainerImageTag=${{ steps.version.outputs.VERSION }}-arm64
      
      - name: Create and push manifest
        run: |
          docker manifest create phmatray/roseline-mcp:${{ steps.version.outputs.VERSION }} \
            phmatray/roseline-mcp:${{ steps.version.outputs.VERSION }}-amd64 \
            phmatray/roseline-mcp:${{ steps.version.outputs.VERSION }}-arm64
          
          docker manifest create phmatray/roseline-mcp:latest \
            phmatray/roseline-mcp:${{ steps.version.outputs.VERSION }}-amd64 \
            phmatray/roseline-mcp:${{ steps.version.outputs.VERSION }}-arm64
          
          docker manifest push phmatray/roseline-mcp:${{ steps.version.outputs.VERSION }}
          docker manifest push phmatray/roseline-mcp:latest
```

## Troubleshooting

### Common Issues and Solutions

#### 1. "unauthorized: authentication required"
- **Solution**: Run `docker login` and ensure credentials are correct

#### 2. "manifest unknown"
- **Solution**: Ensure images are pushed before creating manifest

#### 3. "no matching manifest for platform"
- **Solution**: Build for the target architecture explicitly

#### 4. Build fails with "SDK not found"
- **Solution**: Ensure .NET 9.0 SDK is installed

#### 5. ARM64 build fails on x64 machine
- **Solution**: Install QEMU for cross-platform builds:
  ```bash
  docker run --rm --privileged multiarch/qemu-user-static --reset -p yes
  ```

### Cleanup Commands

```bash
# Remove local images
docker rmi phmatray/roseline-mcp:latest
docker rmi phmatray/roseline-mcp:1.0.0-amd64
docker rmi phmatray/roseline-mcp:1.0.0-arm64

# Remove all unused images
docker image prune -a

# Remove build cache
docker buildx prune
```

## Best Practices

1. **Always test locally** before pushing to Docker Hub
2. **Use semantic versioning** for production releases
3. **Keep images small** by using Alpine base images
4. **Document environment variables** in your README
5. **Use multi-stage builds** for smaller final images
6. **Scan for vulnerabilities** using Docker Scout or similar tools
7. **Set resource limits** in production deployments
8. **Use health checks** for container orchestration
9. **Tag with multiple tags** (version + latest) for convenience
10. **Automate with CI/CD** to ensure consistent builds

## Security Considerations

- Never include secrets or API keys in images
- Use Docker Hub access tokens instead of passwords
- Regularly update base images for security patches
- Scan images for vulnerabilities:
  ```bash
  docker scout cves phmatray/roseline-mcp:latest
  ```
- Use minimal base images (Alpine) to reduce attack surface
- Run containers as non-root user when possible

## Additional Resources

- [.NET Container Documentation](https://learn.microsoft.com/en-us/dotnet/core/containers/overview)
- [Docker Hub Documentation](https://docs.docker.com/docker-hub/)
- [Docker Buildx Documentation](https://docs.docker.com/buildx/working-with-buildx/)
- [Docker Manifest Documentation](https://docs.docker.com/engine/reference/commandline/manifest/)