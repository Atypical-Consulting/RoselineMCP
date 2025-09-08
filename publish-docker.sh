#!/bin/bash
set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
REGISTRY="docker.io"
REPOSITORY="phmatray/roseline-mcp"
PROJECT_PATH="RoselineMCP/RoselineMCP.csproj"

# Function to print colored messages
print_message() {
    echo -e "${2}${1}${NC}"
}

# Function to check if command exists
command_exists() {
    command -v "$1" >/dev/null 2>&1
}

# Check prerequisites
print_message "🔍 Checking prerequisites..." "$BLUE"

if ! command_exists docker; then
    print_message "❌ Docker is not installed. Please install Docker first." "$RED"
    exit 1
fi

if ! command_exists dotnet; then
    print_message "❌ .NET SDK is not installed. Please install .NET 9.0 SDK or later." "$RED"
    exit 1
fi

# Parse command line arguments
VERSION=""
LATEST=false
PUSH=true
PLATFORMS=("x64" "arm64")

while [[ $# -gt 0 ]]; do
    case $1 in
        --version|-v)
            VERSION="$2"
            shift 2
            ;;
        --latest|-l)
            LATEST=true
            shift
            ;;
        --no-push)
            PUSH=false
            shift
            ;;
        --platform|-p)
            IFS=',' read -ra PLATFORMS <<< "$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  -v, --version VERSION    Set the version tag (e.g., 1.0.0)"
            echo "  -l, --latest            Also tag as 'latest'"
            echo "  --no-push               Build only, don't push to registry"
            echo "  -p, --platform PLATFORMS Comma-separated platforms (default: x64,arm64)"
            echo "  -h, --help              Show this help message"
            echo ""
            echo "Examples:"
            echo "  $0 --version 1.0.0 --latest"
            echo "  $0 --version 1.0.0-preview --platform x64"
            echo "  $0 --version nightly-$(date +%Y%m%d) --no-push"
            exit 0
            ;;
        *)
            print_message "Unknown option: $1" "$RED"
            print_message "Use --help to see available options" "$YELLOW"
            exit 1
            ;;
    esac
done

# Check if version is provided
if [ -z "$VERSION" ]; then
    print_message "❌ Version is required. Use --version or -v to specify." "$RED"
    print_message "Example: $0 --version 1.0.0" "$YELLOW"
    exit 1
fi

print_message "📦 Building RoselineMCP Docker images" "$GREEN"
print_message "Version: $VERSION" "$BLUE"
print_message "Platforms: ${PLATFORMS[*]}" "$BLUE"
print_message "Push to registry: $PUSH" "$BLUE"
print_message "Tag as latest: $LATEST" "$BLUE"
echo ""

# Login to Docker Hub if pushing
if [ "$PUSH" = true ]; then
    print_message "🔐 Logging in to Docker Hub..." "$BLUE"
    if ! docker login; then
        print_message "❌ Docker login failed" "$RED"
        exit 1
    fi
    echo ""
fi

# Build for each platform
ARCH_TAGS=()
for PLATFORM in "${PLATFORMS[@]}"; do
    case $PLATFORM in
        x64|amd64)
            ARCH="x64"
            ARCH_SUFFIX="amd64"
            ;;
        arm64|aarch64)
            ARCH="arm64"
            ARCH_SUFFIX="arm64"
            ;;
        *)
            print_message "⚠️ Unknown platform: $PLATFORM, skipping..." "$YELLOW"
            continue
            ;;
    esac
    
    TAG="${VERSION}-${ARCH_SUFFIX}"
    ARCH_TAGS+=("${REPOSITORY}:${TAG}")
    
    print_message "🔨 Building ${ARCH_SUFFIX} image (${REPOSITORY}:${TAG})..." "$BLUE"
    
    BUILD_CMD="dotnet publish ${PROJECT_PATH} \
        --os linux \
        --arch ${ARCH} \
        /t:PublishContainer \
        -c Release \
        -p:ContainerImageTag=${TAG}"
    
    if [ "$PUSH" = true ]; then
        BUILD_CMD="${BUILD_CMD} -p:ContainerRegistry=${REGISTRY}"
    fi
    
    if ! eval $BUILD_CMD; then
        print_message "❌ Failed to build ${ARCH_SUFFIX} image" "$RED"
        exit 1
    fi
    
    print_message "✅ Successfully built ${ARCH_SUFFIX} image" "$GREEN"
    echo ""
done

# Create and push multi-arch manifest if we have multiple architectures
if [ ${#ARCH_TAGS[@]} -gt 1 ] && [ "$PUSH" = true ]; then
    print_message "🔗 Creating multi-architecture manifest..." "$BLUE"
    
    # Create manifest for versioned tag
    MANIFEST_CMD="docker manifest create ${REPOSITORY}:${VERSION}"
    for TAG in "${ARCH_TAGS[@]}"; do
        MANIFEST_CMD="${MANIFEST_CMD} ${TAG}"
    done
    
    if ! eval $MANIFEST_CMD; then
        print_message "❌ Failed to create manifest for ${VERSION}" "$RED"
        exit 1
    fi
    
    # Push versioned manifest
    print_message "⬆️ Pushing manifest ${REPOSITORY}:${VERSION}..." "$BLUE"
    if ! docker manifest push ${REPOSITORY}:${VERSION}; then
        print_message "❌ Failed to push manifest for ${VERSION}" "$RED"
        exit 1
    fi
    
    # Create and push latest manifest if requested
    if [ "$LATEST" = true ]; then
        print_message "🔗 Creating 'latest' manifest..." "$BLUE"
        
        MANIFEST_LATEST_CMD="docker manifest create ${REPOSITORY}:latest"
        for TAG in "${ARCH_TAGS[@]}"; do
            MANIFEST_LATEST_CMD="${MANIFEST_LATEST_CMD} ${TAG}"
        done
        
        if ! eval $MANIFEST_LATEST_CMD; then
            print_message "❌ Failed to create latest manifest" "$RED"
            exit 1
        fi
        
        print_message "⬆️ Pushing manifest ${REPOSITORY}:latest..." "$BLUE"
        if ! docker manifest push ${REPOSITORY}:latest; then
            print_message "❌ Failed to push latest manifest" "$RED"
            exit 1
        fi
    fi
elif [ ${#ARCH_TAGS[@]} -eq 1 ] && [ "$PUSH" = true ]; then
    # Single architecture - just tag it directly
    if [ "$LATEST" = true ]; then
        print_message "🏷️ Tagging as latest..." "$BLUE"
        docker tag ${ARCH_TAGS[0]} ${REPOSITORY}:latest
        docker push ${REPOSITORY}:latest
    fi
    
    # Also create a version tag without architecture suffix
    docker tag ${ARCH_TAGS[0]} ${REPOSITORY}:${VERSION}
    docker push ${REPOSITORY}:${VERSION}
fi

# Summary
echo ""
print_message "🎉 Successfully completed!" "$GREEN"
print_message "📋 Summary:" "$BLUE"
print_message "  Repository: ${REPOSITORY}" "$NC"
print_message "  Version: ${VERSION}" "$NC"

if [ "$PUSH" = true ]; then
    print_message "  Published tags:" "$NC"
    for TAG in "${ARCH_TAGS[@]}"; do
        print_message "    - ${TAG}" "$NC"
    done
    print_message "    - ${REPOSITORY}:${VERSION}" "$NC"
    if [ "$LATEST" = true ]; then
        print_message "    - ${REPOSITORY}:latest" "$NC"
    fi
    
    echo ""
    print_message "🐳 Pull commands:" "$BLUE"
    print_message "  docker pull ${REPOSITORY}:${VERSION}" "$NC"
    if [ "$LATEST" = true ]; then
        print_message "  docker pull ${REPOSITORY}:latest" "$NC"
    fi
else
    print_message "  Images built locally (not pushed)" "$YELLOW"
fi

echo ""
print_message "✨ Done!" "$GREEN"