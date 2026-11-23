cmake_minimum_required(VERSION 3.20)
project(mobmate_vn_ext LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(RNNOISE_DIR "" CACHE PATH "rnnoise source directory")
set(WORLD_DIR "" CACHE PATH "WORLD source directory")
set(WEBRTC_APM_DIR "" CACHE PATH "webrtc-audio-processing source directory")
set(WEBRTC_APM_BUILD_DIR "" CACHE PATH "webrtc-audio-processing build directory")

add_library(mobmate_vn_ext SHARED
  mobmate_vn_ext.cpp
  ${RNNOISE_DIR}/src/denoise.c
  ${RNNOISE_DIR}/src/rnn.c
  ${RNNOISE_DIR}/src/rnnoise_data.c
  ${RNNOISE_DIR}/src/rnnoise_tables.c
  ${RNNOISE_DIR}/src/pitch.c
  ${RNNOISE_DIR}/src/kiss_fft.c
  ${RNNOISE_DIR}/src/celt_lpc.c
  ${RNNOISE_DIR}/src/nnet.c
  ${RNNOISE_DIR}/src/nnet_default.c
  ${RNNOISE_DIR}/src/parse_lpcnet_weights.c
  ${WORLD_DIR}/src/cheaptrick.cpp
  ${WORLD_DIR}/src/common.cpp
  ${WORLD_DIR}/src/d4c.cpp
  ${WORLD_DIR}/src/dio.cpp
  ${WORLD_DIR}/src/fft.cpp
  ${WORLD_DIR}/src/harvest.cpp
  ${WORLD_DIR}/src/matlabfunctions.cpp
  ${WORLD_DIR}/src/stonemask.cpp
  ${WORLD_DIR}/src/synthesis.cpp
)

target_include_directories(mobmate_vn_ext PRIVATE
  ${RNNOISE_DIR}/include
  ${RNNOISE_DIR}/src
  ${WORLD_DIR}/src
  ${WEBRTC_APM_DIR}
  ${WEBRTC_APM_DIR}/webrtc
  ${WEBRTC_APM_DIR}/subprojects/abseil-cpp-20240722.0
)

if(MSVC)
  target_compile_options(mobmate_vn_ext PRIVATE /utf-8 /wd4244 /wd4251 /wd4275 /wd4005 /wd4267 /wd4715)
  target_compile_definitions(mobmate_vn_ext PRIVATE NOMINMAX _WINSOCKAPI_ WEBRTC_WIN)
endif()

if(WIN32)
  target_link_directories(mobmate_vn_ext PRIVATE ${WEBRTC_APM_BUILD_DIR}/webrtc/modules/audio_processing)
  target_link_libraries(mobmate_vn_ext PRIVATE webrtc-audio-processing-2 winmm)
else()
  find_library(WEBRTC_APM_LIB NAMES webrtc-audio-processing-2 webrtc-audio-processing HINTS ${WEBRTC_APM_BUILD_DIR}/webrtc/modules/audio_processing ${WEBRTC_APM_BUILD_DIR})
  if(WEBRTC_APM_LIB)
    target_link_libraries(mobmate_vn_ext PRIVATE ${WEBRTC_APM_LIB} m)
  else()
    target_link_libraries(mobmate_vn_ext PRIVATE m)
  endif()
endif()
