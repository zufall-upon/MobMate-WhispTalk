cmake_minimum_required(VERSION 3.20)
project(mobmate_native_core LANGUAGES C CXX)

# mobmate_native_core library
if(WIN32)
  set(PLATFORM_AUDIO_SRC src/audio_capture.cpp)
else()
  set(PLATFORM_AUDIO_SRC src/audio_capture_linux.cpp)
endif()

add_library(mobmate_native_core
  src/core.cpp
  src/wave.cpp
  src/manifest.cpp
  src/metrics.cpp
  src/whisper_bridge.cpp
  src/audio_prefilter.cpp
  src/audio_prefilter_strong.cpp
  ${PLATFORM_AUDIO_SRC}
  src/moonshine_bridge.cpp
  src/silero_vad.cpp
  src/speaker_profile.cpp
)

# Required for linking static lib into shared lib on Linux
set_target_properties(mobmate_native_core PROPERTIES POSITION_INDEPENDENT_CODE ON)

target_include_directories(mobmate_native_core
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../libs/onnxruntime/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../vendor  # miniaudio.h (Linux audio capture)
)

target_compile_features(mobmate_native_core PUBLIC cxx_std_20)

# MSVC: treat source and char literals as UTF-8
if(MSVC)
  target_compile_options(mobmate_native_core PRIVATE /utf-8)
endif()

if(WIN32)
  target_link_libraries(mobmate_native_core PRIVATE winmm ole32 oleaut32)
else()
  # Linux: pthreads + dl + math (required by miniaudio and std::filesystem)
  find_package(Threads REQUIRED)
  target_link_libraries(mobmate_native_core PRIVATE Threads::Threads dl m)
endif()

# mobmate-native CLI executable
add_executable(mobmate-native
  src/main.cpp
)

target_link_libraries(mobmate-native
  PRIVATE
    mobmate_native_core
)

# MSVC: UTF-8 for CLI as well
if(MSVC)
  target_compile_options(mobmate-native PRIVATE /utf-8)
endif()

# sherpa-onnx C API import library (for SenseVoice offline ASR)
if(WIN32)
  set(SHERPA_ONNX_LIB "${CMAKE_CURRENT_SOURCE_DIR}/../../libs/sherpa-onnx/sherpa-onnx-c-api.lib")
else()
  # Linux: prefer shared library; fall back to static
  find_library(SHERPA_ONNX_LIB
    NAMES sherpa-onnx-c-api
    HINTS
      "${CMAKE_CURRENT_SOURCE_DIR}/../../libs/sherpa-onnx/lib"
      "/usr/local/lib"
      "/usr/lib/x86_64-linux-gnu"
      "/usr/lib"
  )
  if(NOT SHERPA_ONNX_LIB)
    message(WARNING "sherpa-onnx-c-api not found; SenseVoice will be unavailable")
    set(SHERPA_ONNX_LIB "")
  endif()
endif()
set(SHERPA_ONNX_INCLUDE "${CMAKE_CURRENT_SOURCE_DIR}/../../libs/sherpa-onnx/include")

# mobmate_native_bridge shared library (DLL on Windows)
add_library(mobmate_native_bridge SHARED
  src/native_api.cpp
  src/voice_conversion.cpp
)

target_include_directories(mobmate_native_bridge
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
  PRIVATE
    ${SHERPA_ONNX_INCLUDE}
    ${CMAKE_CURRENT_SOURCE_DIR}/../../libs/onnxruntime/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../vendor  # miniaudio.h
)

target_compile_features(mobmate_native_bridge PUBLIC cxx_std_20)

# Link against the static core library for version string etc.
if(SHERPA_ONNX_LIB)
  target_link_libraries(mobmate_native_bridge
    PRIVATE
      mobmate_native_core
      ${SHERPA_ONNX_LIB}
  )
else()
  target_link_libraries(mobmate_native_bridge
    PRIVATE
      mobmate_native_core
  )
endif()

# MSVC: UTF-8 for bridge as well
if(MSVC)
  target_compile_options(mobmate_native_bridge PRIVATE /utf-8)
endif()

# Focused offline regression targets. They are never installed or built by default.
add_executable(mobmate_pcm_owner_regression EXCLUDE_FROM_ALL
  ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/pcm_playback_owner_regression.cpp)
target_include_directories(mobmate_pcm_owner_regression PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_features(mobmate_pcm_owner_regression PRIVATE cxx_std_20)
set_target_properties(mobmate_pcm_owner_regression PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/pcm-owner-tests")
if(NOT WIN32)
  target_link_libraries(mobmate_pcm_owner_regression PRIVATE Threads::Threads)
endif()
if(WIN32)
  foreach(onset_variant IN ITEMS new old)
    add_library(mobmate_onset_mock_${onset_variant} SHARED EXCLUDE_FROM_ALL
      ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/onset_native_mock.cpp)
    set_target_properties(mobmate_onset_mock_${onset_variant} PROPERTIES OUTPUT_NAME mobmate_native_bridge
      RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/onset-mock/${onset_variant}"
      ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/onset-mock/${onset_variant}"
      PDB_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/onset-mock/${onset_variant}")
  endforeach()
  target_compile_definitions(mobmate_onset_mock_old PRIVATE ONSET_MOCK_OLD=1)
  add_library(mobmate_addon_mock_moonshine SHARED EXCLUDE_FROM_ALL
    ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/addon_moonshine_mock.cpp)
  set_target_properties(mobmate_addon_mock_moonshine PROPERTIES OUTPUT_NAME moonshine
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/addon-asr-tests")
  add_library(mobmate_addon_mock_whisper SHARED EXCLUDE_FROM_ALL
    ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/addon_whisper_mock.cpp)
  target_include_directories(mobmate_addon_mock_whisper PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
  set_target_properties(mobmate_addon_mock_whisper PROPERTIES OUTPUT_NAME whisper
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/addon-asr-tests")
  add_executable(mobmate_addon_native_regression EXCLUDE_FROM_ALL
    ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/addon_native_regression.cpp)
  target_include_directories(mobmate_addon_native_regression PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include ${SHERPA_ONNX_INCLUDE}
    ${CMAKE_CURRENT_SOURCE_DIR}/../../libs/onnxruntime/include ${CMAKE_CURRENT_SOURCE_DIR}/../vendor)
  target_compile_features(mobmate_addon_native_regression PRIVATE cxx_std_20)
  target_compile_options(mobmate_addon_mock_moonshine PRIVATE /utf-8)
  target_compile_options(mobmate_addon_mock_whisper PRIVATE /utf-8)
  target_compile_options(mobmate_addon_native_regression PRIVATE /utf-8)
  target_link_libraries(mobmate_addon_native_regression PRIVATE mobmate_native_core ${SHERPA_ONNX_LIB})
  add_dependencies(mobmate_addon_native_regression mobmate_addon_mock_moonshine mobmate_addon_mock_whisper)
  set_target_properties(mobmate_addon_native_regression PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/addon-asr-tests")
  # The test executable links this existing runtime; keep it beside the test,
  # not in the application deployment or the developer's global PATH.
  set(MOBMATE_ADDON_TEST_SHERPA_DLL "${CMAKE_CURRENT_SOURCE_DIR}/../../libs/sherpa-onnx/sherpa-onnx-c-api.dll" CACHE FILEPATH "Existing sherpa runtime for the offline regression")
  set(MOBMATE_ADDON_TEST_ONNX_DLL "${CMAKE_CURRENT_SOURCE_DIR}/../../libs/sherpa-onnx/onnxruntime.dll" CACHE FILEPATH "Existing ONNX runtime for the offline regression")
  add_custom_command(TARGET mobmate_addon_native_regression POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "${MOBMATE_ADDON_TEST_SHERPA_DLL}"
      "$<TARGET_FILE_DIR:mobmate_addon_native_regression>"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      "${MOBMATE_ADDON_TEST_ONNX_DLL}"
      "$<TARGET_FILE_DIR:mobmate_addon_native_regression>")
endif()

# Device-free VC control regression. Includes the actual VC translation unit;
# only recognizer, model inference and VAD probabilities are simulated. No DLL
# deployment, model download, audio device or default/package build dependency.
add_executable(mobmate_mobfy_segment_regression EXCLUDE_FROM_ALL
  ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/mobfy_segment_boundary_regression.cpp)
target_include_directories(mobmate_mobfy_segment_regression PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}/include
  ${CMAKE_CURRENT_SOURCE_DIR}/../../libs/onnxruntime/include
  ${CMAKE_CURRENT_SOURCE_DIR}/../vendor)
target_compile_features(mobmate_mobfy_segment_regression PRIVATE cxx_std_20)
target_link_libraries(mobmate_mobfy_segment_regression PRIVATE mobmate_native_core)
if(MSVC)
  target_compile_options(mobmate_mobfy_segment_regression PRIVATE /utf-8)
endif()
if(MOBMATE_VC_BASELINE_SOURCE)
  add_executable(mobmate_mobfy_segment_baseline EXCLUDE_FROM_ALL
    ${CMAKE_CURRENT_SOURCE_DIR}/../../tests/MobMate.Core.Tests/mobfy_segment_boundary_regression.cpp)
  target_include_directories(mobmate_mobfy_segment_baseline PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include ${CMAKE_CURRENT_SOURCE_DIR}/../../libs/onnxruntime/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../vendor)
  target_compile_definitions(mobmate_mobfy_segment_baseline PRIVATE
    MOBMATE_VC_BOUNDARY_BASELINE MOBMATE_VC_SOURCE="${MOBMATE_VC_BASELINE_SOURCE}")
  target_link_libraries(mobmate_mobfy_segment_baseline PRIVATE mobmate_native_core)
  if(MSVC)
    target_compile_options(mobmate_mobfy_segment_baseline PRIVATE /utf-8)
  endif()
endif()

# Testing
if(BUILD_TESTING)
  enable_testing()
  if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/tests/CMakeLists.txt")
    add_subdirectory(tests)
  endif()
endif()
