cmake_minimum_required(VERSION 3.22.1)
project("audiobridge_native")

find_package(oboe REQUIRED CONFIG)

add_library(audiobridge_native SHARED
    jni_bridge.cpp
    receiver.cpp
    audio_engine.cpp
)

target_include_directories(audiobridge_native PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
)

target_compile_options(audiobridge_native PRIVATE
    -Wall -Wextra -Werror=return-type
    -O3 -ffast-math -fvisibility=hidden
)

target_link_libraries(audiobridge_native PRIVATE
    oboe::oboe
    log
    android
)
