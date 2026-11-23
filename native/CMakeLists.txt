cmake_minimum_required(VERSION 3.25)
project(MobMateV2 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(BUILD_TESTING "Build tests" OFF)

add_subdirectory(mobmate_native_core)
