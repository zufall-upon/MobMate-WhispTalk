#!/bin/bash
# Build the existing ASR runtimes as an isolated candidate; never replace an
# installed runtime. The caller must provide an existing Vulkan SDK/toolchain.
set -euo pipefail

if [ "$#" -ne 2 ]; then
    echo "usage: bash scripts/linux/prepare-asr-runtime.sh ABSOLUTE_NEW_CANDIDATE_DIR ABSOLUTE_DOWNLOAD_CACHE" >&2
    exit 2
fi
candidate="$1"
cache="$2"
case "$candidate" in /*) ;; *) echo "candidate must be absolute" >&2; exit 2;; esac
case "$cache" in /*) ;; *) echo "cache must be absolute" >&2; exit 2;; esac
[ ! -e "$candidate" ] && [ ! -L "$candidate" ] || { echo "candidate already exists: $candidate" >&2; exit 1; }
[ -d "$cache" ] || { echo "download cache missing: $cache" >&2; exit 1; }
[ "$(uname -sm)" = 'Linux x86_64' ] || { echo 'only Linux x86_64 is supported' >&2; exit 1; }
configuration="${MOBMATE_ASR_CONFIGURATION:-Debug}"
case "$configuration" in Debug|Release) ;; *) echo 'MOBMATE_ASR_CONFIGURATION must be Debug or Release' >&2; exit 2;; esac

# Explicit paths avoid PATH discovery and do not install an SDK or toolchain.
: "${MOBMATE_CMAKE:?set MOBMATE_CMAKE to the existing cmake executable}"
: "${MOBMATE_VULKAN_INCLUDE:?set MOBMATE_VULKAN_INCLUDE to the existing Vulkan headers}"
: "${MOBMATE_VULKAN_LIBRARY:?set MOBMATE_VULKAN_LIBRARY to the existing Vulkan loader}"
: "${MOBMATE_VULKAN_GLSLC:?set MOBMATE_VULKAN_GLSLC to the existing shader compiler}"
: "${MOBMATE_SPIRV_HEADERS:?set MOBMATE_SPIRV_HEADERS to the existing SPIRV-Headers CMake directory}"
[ -x "$MOBMATE_CMAKE" ] && [ -x "$MOBMATE_VULKAN_GLSLC" ] &&
    [ -f "$MOBMATE_VULKAN_INCLUDE/vulkan/vulkan.h" ] && [ -f "$MOBMATE_VULKAN_LIBRARY" ] &&
    [ -f "$MOBMATE_SPIRV_HEADERS/SPIRV-HeadersConfig.cmake" ] || {
    echo 'existing CMake/Vulkan SDK inputs are incomplete' >&2; exit 1;
}
"$MOBMATE_VULKAN_GLSLC" --version

mkdir -p "$candidate/downloads" "$candidate/src" "$candidate/build" "$candidate/runtime"
fetch() {
    local name="$1" digest="$2" url="$3" file="$candidate/downloads/$1"
    if [ -f "$cache/$name" ]; then
        cp "$cache/$name" "$file"
    else
        /usr/bin/curl --fail --location --retry 2 --connect-timeout 20 --max-time 600 --output "$file" "$url"
    fi
    printf '%s  %s\n' "$digest" "$file" | /usr/bin/sha256sum --check --status || {
        echo "SHA256 mismatch: $name" >&2; exit 1;
    }
}

fetch whisper-d1be6fde11ac6e0407606b4e42fe72d34add8037.tar.gz \
    645445da884df6dbd52b30f82d7be7c01d1767c938714e477e786b88d529e9f4 \
    https://codeload.github.com/ggml-org/whisper.cpp/tar.gz/d1be6fde11ac6e0407606b4e42fe72d34add8037
fetch moonshine-d8d8fa29d05ca67441a7719e70099f9f48917a93.tar.gz \
    857f04ed504d24e5b3fff4a0ce7066d57429a30d54714d39a587aa15e950ff30 \
    https://codeload.github.com/moonshine-ai/moonshine/tar.gz/d8d8fa29d05ca67441a7719e70099f9f48917a93
fetch sherpa-af2d8d81dbfa61c4cea4c568da78f3982edca005.tar.gz \
    afcafe3c34bc2f15a363ad791fa1644a69bf0977a58c1b4f617468976c2faeb5 \
    https://codeload.github.com/k2-fsa/sherpa-onnx/tar.gz/af2d8d81dbfa61c4cea4c568da78f3982edca005
fetch microsoft.ml.onnxruntime.1.31.1.nupkg \
    6871ccbc97f3d5bdb4095a1dcec33aa78d56f6ed8de5980ef5fa1d9f2e7c679a \
    https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/1.31.1/microsoft.ml.onnxruntime.1.31.1.nupkg

for name in whisper moonshine sherpa; do mkdir -p "$candidate/src/$name"; done
/usr/bin/tar -xzf "$candidate/downloads/whisper-d1be6fde11ac6e0407606b4e42fe72d34add8037.tar.gz" --strip-components=1 -C "$candidate/src/whisper"
/usr/bin/tar -xzf "$candidate/downloads/moonshine-d8d8fa29d05ca67441a7719e70099f9f48917a93.tar.gz" --strip-components=1 -C "$candidate/src/moonshine"
/usr/bin/tar -xzf "$candidate/downloads/sherpa-af2d8d81dbfa61c4cea4c568da78f3982edca005.tar.gz" --strip-components=1 -C "$candidate/src/sherpa"

mkdir -p "$candidate/ort/include" "$candidate/ort/lib" "$candidate/runtime/shared"
/usr/bin/unzip -j -q "$candidate/downloads/microsoft.ml.onnxruntime.1.31.1.nupkg" 'build/native/include/*' -d "$candidate/ort/include"
/usr/bin/unzip -j -q "$candidate/downloads/microsoft.ml.onnxruntime.1.31.1.nupkg" 'runtimes/linux-x64/native/libonnxruntime.so' -d "$candidate/ort/lib"
cp "$candidate/ort/lib/libonnxruntime.so" "$candidate/runtime/shared/libonnxruntime.so.1"
ln -s libonnxruntime.so.1 "$candidate/runtime/shared/libonnxruntime.so"

# Official Linux Moonshine and sherpa binaries require versioned ORT symbols
# VERS_1.23.2 and VERS_1.28.2 respectively. Rebuild both against the one
# ORT 1.31.1 instead of silently mixing the official prebuilt .so files.
cp "$candidate/ort/lib/libonnxruntime.so" "$candidate/src/moonshine/core/third-party/onnxruntime/lib/linux/x86_64/libonnxruntime.so.1"
"$MOBMATE_CMAKE" -S "$candidate/src/moonshine/core" -B "$candidate/build/moonshine" \
    -DCMAKE_BUILD_TYPE="$configuration" -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON '-DCMAKE_INSTALL_RPATH=$ORIGIN'
"$MOBMATE_CMAKE" --build "$candidate/build/moonshine" --target moonshine --parallel "${MOBMATE_BUILD_JOBS:-2}"
cp "$candidate/build/moonshine/libmoonshine.so" "$candidate/runtime/shared/"

SHERPA_ONNXRUNTIME_INCLUDE_DIR="$candidate/ort/include" SHERPA_ONNXRUNTIME_LIB_DIR="$candidate/ort/lib" \
    "$MOBMATE_CMAKE" -S "$candidate/src/sherpa" -B "$candidate/build/sherpa" \
    -DCMAKE_BUILD_TYPE="$configuration" -DBUILD_SHARED_LIBS=ON -DSHERPA_ONNX_USE_PRE_INSTALLED_ONNXRUNTIME_IF_AVAILABLE=ON \
    -DSHERPA_ONNX_ENABLE_BINARY=OFF -DSHERPA_ONNX_ENABLE_TESTS=OFF -DSHERPA_ONNX_ENABLE_PYTHON=OFF \
    -DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF -DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF -DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF \
    -DSHERPA_ONNX_ENABLE_C_API=ON -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON '-DCMAKE_INSTALL_RPATH=$ORIGIN'
"$MOBMATE_CMAKE" --build "$candidate/build/sherpa" --target sherpa-onnx-c-api --parallel "${MOBMATE_BUILD_JOBS:-2}"
cp "$candidate/build/sherpa/lib/libsherpa-onnx-c-api.so" "$candidate/runtime/shared/"

for variant in cpu vulkan; do
    vk=OFF
    [ "$variant" != vulkan ] || vk=ON
    "$MOBMATE_CMAKE" -S "$candidate/src/whisper" -B "$candidate/build/whisper-$variant" \
        -DCMAKE_BUILD_TYPE="$configuration" -DBUILD_SHARED_LIBS=ON -DGGML_NATIVE=OFF -DGGML_VULKAN="$vk" \
        -DWHISPER_BUILD_IS_DEV=OFF -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=OFF \
        -DVulkan_INCLUDE_DIR="$MOBMATE_VULKAN_INCLUDE" -DVulkan_LIBRARY="$MOBMATE_VULKAN_LIBRARY" \
        -DVulkan_GLSLC_EXECUTABLE="$MOBMATE_VULKAN_GLSLC" -DSPIRV-Headers_DIR="$MOBMATE_SPIRV_HEADERS" \
        -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON '-DCMAKE_INSTALL_RPATH=$ORIGIN'
    "$MOBMATE_CMAKE" --build "$candidate/build/whisper-$variant" --target whisper --parallel "${MOBMATE_BUILD_JOBS:-2}"
    mkdir -p "$candidate/runtime/whisper-$variant"
    find "$candidate/build/whisper-$variant/bin" -maxdepth 1 \( -type f -o -type l \) -name '*.so*' \
        -exec cp -a '{}' "$candidate/runtime/whisper-$variant/" ';'
done

for lib in "$candidate/runtime/shared/libmoonshine.so" "$candidate/runtime/shared/libsherpa-onnx-c-api.so"; do
    result=$(LD_LIBRARY_PATH="$candidate/runtime/shared" /usr/bin/ldd -r "$lib" 2>&1) || { echo "$result" >&2; exit 1; }
    if grep -Eq 'undefined symbol:|not found|version .* not found' <<< "$result"; then echo "$result" >&2; exit 1; fi
done
[ -f "$candidate/runtime/whisper-vulkan/libggml-vulkan.so.0.26.0" ] || { echo 'Vulkan backend missing' >&2; exit 1; }
[ -f "$candidate/runtime/whisper-cpu/libwhisper.so.1.9.5" ] || { echo 'CPU Whisper missing' >&2; exit 1; }
(cd "$candidate" && find runtime -type f -exec sha256sum '{}' + | sort) > "$candidate/runtime.sha256"
printf 'candidate ready: %s\nversions: Whisper 1.9.5 / ggml 0.26.0, Moonshine 0.1.6, sherpa 2.0.1, ORT 1.31.1\n' "$candidate"
printf 'native ASR configuration: %s\n' "$configuration"
for library in "$candidate/runtime/shared/libmoonshine.so" "$candidate/runtime/shared/libsherpa-onnx-c-api.so" "$candidate/runtime/whisper-vulkan/libggml-vulkan.so.0.26.0"; do
    required_glibc=$(/usr/bin/objdump -T "$library" | grep -oE 'GLIBC_[0-9]+(\.[0-9]+)+' | sort -V | tail -1 || true)
    printf 'host ABI %s: %s\n' "$(basename "$library")" "${required_glibc:-unknown}"
done
printf 'This is an isolated candidate, not a package or a Windows build.\n'
