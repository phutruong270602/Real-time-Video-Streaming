cmake_minimum_required(VERSION 3.21)

project(OptitrackRtspReceiver VERSION 0.1.0 LANGUAGES CXX)

if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()

set(FFMPEG_ROOT "" CACHE PATH "FFmpeg SDK root (contains include and lib directories)")

find_path(FFMPEG_INCLUDE_DIR
    NAMES libavformat/avformat.h
    HINTS "${FFMPEG_ROOT}/include"
    REQUIRED
)

find_library(AVFORMAT_LIBRARY
    NAMES avformat
    HINTS "${FFMPEG_ROOT}/lib/x64" "${FFMPEG_ROOT}/lib"
    REQUIRED
)
find_library(AVCODEC_LIBRARY
    NAMES avcodec
    HINTS "${FFMPEG_ROOT}/lib/x64" "${FFMPEG_ROOT}/lib"
    REQUIRED
)
find_library(AVUTIL_LIBRARY
    NAMES avutil
    HINTS "${FFMPEG_ROOT}/lib/x64" "${FFMPEG_ROOT}/lib"
    REQUIRED
)
find_library(AVSWRESAMPLE_LIBRARY
    NAMES swresample
    HINTS "${FFMPEG_ROOT}/lib/x64" "${FFMPEG_ROOT}/lib"
    REQUIRED
)
find_library(AVSWSCALE_LIBRARY
    NAMES swscale
    HINTS "${FFMPEG_ROOT}/lib/x64" "${FFMPEG_ROOT}/lib"
    REQUIRED
)

add_library(optitrack_rtsp_receiver
    src/RtspReceiver.cpp
)
add_library(OptitrackRtspReceiver::receiver ALIAS optitrack_rtsp_receiver)

target_compile_features(optitrack_rtsp_receiver PUBLIC cxx_std_17)
target_include_directories(optitrack_rtsp_receiver
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
)
target_include_directories(optitrack_rtsp_receiver SYSTEM PRIVATE "${FFMPEG_INCLUDE_DIR}")
target_link_libraries(optitrack_rtsp_receiver
    PUBLIC
        "${AVFORMAT_LIBRARY}"
        "${AVCODEC_LIBRARY}"
        "${AVUTIL_LIBRARY}"
        "${AVSWRESAMPLE_LIBRARY}"
)

if(WIN32)
    target_link_libraries(
        optitrack_rtsp_receiver
        PUBLIC ws2_32 bcrypt secur32 crypt32 mfplat mfuuid strmiids
    )
endif()

if(MSVC)
    target_compile_options(optitrack_rtsp_receiver PRIVATE /W4 /permissive-)
else()
    target_compile_options(optitrack_rtsp_receiver PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_executable(optitrack_rtsp_receiver_cli
    tools/receiver_cli.cpp
)
target_link_libraries(optitrack_rtsp_receiver_cli
    PRIVATE OptitrackRtspReceiver::receiver
)

add_library(optitrack_rtsp_native SHARED
    src/RtspReceiverC.cpp
)
set_target_properties(optitrack_rtsp_native PROPERTIES OUTPUT_NAME OptitrackRtspNative)
target_compile_features(optitrack_rtsp_native PRIVATE cxx_std_17)
target_compile_definitions(optitrack_rtsp_native PRIVATE
    OPTITRACK_RTSP_NATIVE_EXPORTS
    "OPTITRACK_RTSP_LOG_FILE=\"${CMAKE_CURRENT_SOURCE_DIR}/log/rtsp_receiver.log\""
)
target_include_directories(optitrack_rtsp_native
    PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include"
)
target_include_directories(optitrack_rtsp_native SYSTEM PRIVATE "${FFMPEG_INCLUDE_DIR}")
target_link_libraries(optitrack_rtsp_native
    PRIVATE
        OptitrackRtspReceiver::receiver
        "${AVSWSCALE_LIBRARY}"
)

if(MSVC)
    target_compile_options(optitrack_rtsp_receiver_cli PRIVATE /W4 /permissive-)
    target_compile_options(optitrack_rtsp_native PRIVATE /W4 /permissive-)
else()
    target_compile_options(optitrack_rtsp_receiver_cli PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(optitrack_rtsp_native PRIVATE -Wall -Wextra -Wpedantic)
endif()
