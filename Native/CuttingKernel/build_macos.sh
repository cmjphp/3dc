#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUTPUT="$PROJECT_ROOT/Assets/Plugins/cutting_kernel_v43.dylib"
BUILD_DIR="$SCRIPT_DIR/build/macos"
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

mkdir -p "$PROJECT_ROOT/Assets/Plugins"

cmake -S "$SCRIPT_DIR" -B "$BUILD_DIR" \
  -Wno-dev \
  -DCMAKE_BUILD_TYPE=Release \
  -DCUTTING_KERNEL_USE_OPENVDB=ON

cmake --build "$BUILD_DIR" --config Release

cp "$BUILD_DIR/cutting_kernel_v43.dylib" "$OUTPUT"
xattr -c "$OUTPUT" 2>/dev/null || true
codesign --force --sign - "$OUTPUT"

echo "Built $OUTPUT"
