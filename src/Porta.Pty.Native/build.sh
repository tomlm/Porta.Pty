#!/bin/bash
# Build script for porta_pty native library
# Run this on each target platform to build the native library

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
OUTPUT_DIR="$SCRIPT_DIR/output"

# Detect platform
if [[ "$OSTYPE" == "darwin"* ]]; then
    PLATFORM="osx"
    LIB_EXT="dylib"
    
    # macOS can cross-compile for both architectures
    for ARCH in "x86_64" "arm64"; do
        if [[ "$ARCH" == "arm64" ]]; then
            RID="osx-arm64"
        else
            RID="osx-x64"
        fi
        
        echo "Building for $RID..."
        
        ARCH_BUILD_DIR="$BUILD_DIR/$RID"
        rm -rf "$ARCH_BUILD_DIR"
        mkdir -p "$ARCH_BUILD_DIR"
        cd "$ARCH_BUILD_DIR"
        
        # Configure with architecture flag
        cmake -DCMAKE_BUILD_TYPE=Release \
              -DCMAKE_OSX_ARCHITECTURES="$ARCH" \
              "$SCRIPT_DIR"
        cmake --build . --config Release
        
        # Copy to output directory
        RUNTIME_DIR="$OUTPUT_DIR/runtimes/$RID/native"
        mkdir -p "$RUNTIME_DIR"
        cp "$ARCH_BUILD_DIR/bin/libporta_pty.$LIB_EXT" "$RUNTIME_DIR/"
        
        echo "Built: $RUNTIME_DIR/libporta_pty.$LIB_EXT"
    done
    
elif [[ "$OSTYPE" == "linux"* ]]; then
    PLATFORM="linux"
    LIB_EXT="so"
    
    # Detect current architecture
    CURRENT_ARCH=$(uname -m)
    
    # Build for current architecture
    if [[ "$CURRENT_ARCH" == "aarch64" ]]; then
        RID="linux-arm64"
    else
        RID="linux-x64"
    fi
    
    echo "Building for $RID (native)..."
    
    ARCH_BUILD_DIR="$BUILD_DIR/$RID"
    rm -rf "$ARCH_BUILD_DIR"
    mkdir -p "$ARCH_BUILD_DIR"
    cd "$ARCH_BUILD_DIR"
    
    cmake -DCMAKE_BUILD_TYPE=Release "$SCRIPT_DIR"
    cmake --build . --config Release
    
    RUNTIME_DIR="$OUTPUT_DIR/runtimes/$RID/native"
    mkdir -p "$RUNTIME_DIR"
    cp "$ARCH_BUILD_DIR/bin/libporta_pty.$LIB_EXT" "$RUNTIME_DIR/"
    
    echo "Built: $RUNTIME_DIR/libporta_pty.$LIB_EXT"
    
    # Try cross-compile for other architecture if toolchain is available
    if [[ "$CURRENT_ARCH" == "x86_64" ]]; then
        CROSS_RID="linux-arm64"
        CROSS_COMPILER="aarch64-linux-gnu-gcc"
    else
        CROSS_RID="linux-x64"
        CROSS_COMPILER="x86_64-linux-gnu-gcc"
    fi
    
    if command -v "$CROSS_COMPILER" &> /dev/null; then
        echo "Cross-compiling for $CROSS_RID..."
        
        CROSS_BUILD_DIR="$BUILD_DIR/$CROSS_RID"
        rm -rf "$CROSS_BUILD_DIR"
        mkdir -p "$CROSS_BUILD_DIR"
        cd "$CROSS_BUILD_DIR"
        
        # Create toolchain file
        cat > toolchain.cmake << EOF
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_C_COMPILER $CROSS_COMPILER)
EOF
        
        cmake -DCMAKE_BUILD_TYPE=Release \
              -DCMAKE_TOOLCHAIN_FILE=toolchain.cmake \
              "$SCRIPT_DIR"
        cmake --build . --config Release
        
        RUNTIME_DIR="$OUTPUT_DIR/runtimes/$CROSS_RID/native"
        mkdir -p "$RUNTIME_DIR"
        cp "$CROSS_BUILD_DIR/bin/libporta_pty.$LIB_EXT" "$RUNTIME_DIR/"
        
        echo "Built: $RUNTIME_DIR/libporta_pty.$LIB_EXT"
    else
        echo "Cross-compiler $CROSS_COMPILER not found, skipping $CROSS_RID"
    fi
else
    echo "Unsupported platform: $OSTYPE"
    exit 1
fi

echo ""
echo "Build complete!"
echo "Output directory: $OUTPUT_DIR/runtimes/"
ls -la "$OUTPUT_DIR/runtimes/"*/native/ 2>/dev/null || true
