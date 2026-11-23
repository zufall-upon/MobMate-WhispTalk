#!/bin/bash
# Build libmobmate_native_bridge.so and mobmate-native for Linux.
# Run this inside the distrobox (mobmate-dev) on Bazzite, or on any Ubuntu 22.04+ host
# with cmake + g++ (>= 11) available.
#
# Usage:
#   distrobox enter mobmate-dev -- bash -lc "bash scripts/linux/build-native-bridge.sh"
#
# Output is deployed to the publish dir automatically. The prepared candidate
# supplies the matching sherpa and ORT libraries; no old NuGet fallback is used.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
NATIVE_DIR="$REPO/native"
: "${MOBMATE_ASR_RUNTIME:?set MOBMATE_ASR_RUNTIME to the prepared candidate}"
: "${MOBMATE_CMAKE:?set MOBMATE_CMAKE to the existing cmake executable}"
[ -x "$MOBMATE_CMAKE" ] || { echo "ERROR: cmake executable missing: $MOBMATE_CMAKE" >&2; exit 1; }
BUILD_CONFIGURATION="${MOBMATE_NATIVE_CONFIGURATION:-Debug}"
case "$BUILD_CONFIGURATION" in Debug|Release) ;; *) echo "ERROR: expected Debug or Release" >&2; exit 1;; esac
SOURCE_ID=$(printf '%s' "$REPO" | sha256sum | cut -c1-16)
BUILD_DIR="$MOBMATE_ASR_RUNTIME/build/mobmate-native-$BUILD_CONFIGURATION-$SOURCE_ID"
SHERPA_NATIVE="$MOBMATE_ASR_RUNTIME/runtime/shared"
PUBLISH_DIR="$REPO/src/MobMate.App.Linux/bin/$BUILD_CONFIGURATION/net8.0/linux-x64/publish/mobmate_native_core"

echo "=== mobmate_native_bridge Linux build ==="
echo "Repo   : $REPO"
echo "Build  : $BUILD_DIR"
echo "Deploy : $PUBLISH_DIR"

if [ ! -f "$SHERPA_NATIVE/libsherpa-onnx-c-api.so" ]; then
    echo "ERROR: sherpa-onnx native lib not found at $SHERPA_NATIVE"
    echo "  Make sure the .NET project has been restored at least once (dotnet restore src/MobMate.App.Linux)"
    exit 1
fi

"$MOBMATE_CMAKE" --version | head -1
g++ --version | head -1

mkdir -p "$BUILD_DIR"

"$MOBMATE_CMAKE" -S "$NATIVE_DIR" \
  -B "$BUILD_DIR" \
  -G "Unix Makefiles" \
  -DCMAKE_BUILD_TYPE="$BUILD_CONFIGURATION" \
  -DBUILD_TESTING=OFF \
  -DSHERPA_ONNX_LIB="$SHERPA_NATIVE/libsherpa-onnx-c-api.so"

"$MOBMATE_CMAKE" --build "$BUILD_DIR" --target mobmate_native_bridge mobmate-native -j"${MOBMATE_BUILD_JOBS:-2}"

SO="$BUILD_DIR/mobmate_native_core/libmobmate_native_bridge.so"
EXE="$BUILD_DIR/mobmate_native_core/mobmate-native"

echo "=== Build complete ==="
ls -lh "$SO" "$EXE"

mkdir -p "$PUBLISH_DIR"
cp "$SO" "$PUBLISH_DIR/"
cp "$EXE" "$PUBLISH_DIR/"
echo "=== Deployed to $PUBLISH_DIR ==="
ls -lh "$PUBLISH_DIR/"
