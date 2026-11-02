#pragma once

#include <stdint.h>

#if defined(_WIN32)
#if defined(OPTITRACK_RTSP_NATIVE_EXPORTS)
#define OPTITRACK_RTSP_API __declspec(dllexport)
#elif defined(OPTITRACK_RTSP_NATIVE_IMPORTS)
#define OPTITRACK_RTSP_API __declspec(dllimport)
#else
#define OPTITRACK_RTSP_API
#endif
#else
#define OPTITRACK_RTSP_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct OptitrackRtspStats {
    int running;
    int connected;
    int width;
    int height;
    uint64_t packets_received;
    uint64_t bytes_received;
    uint64_t packets_dropped;
    uint64_t frames_decoded;
} OptitrackRtspStats;

OPTITRACK_RTSP_API void* optitrack_rtsp_create(void);
OPTITRACK_RTSP_API void optitrack_rtsp_destroy(void* handle);
OPTITRACK_RTSP_API int optitrack_rtsp_start(
    void* handle,
    const char* url,
    int use_tcp,
    char* error,
    int error_capacity);
OPTITRACK_RTSP_API int optitrack_rtsp_start_ex(
    void* handle,
    const char* url,
    int use_tcp,
    int timeout_ms,
    int reconnect,
    int reconnect_delay_ms,
    int queue_capacity,
    char* error,
    int error_capacity);
OPTITRACK_RTSP_API void optitrack_rtsp_stop(void* handle);
OPTITRACK_RTSP_API int optitrack_rtsp_get_stats(void* handle, OptitrackRtspStats* stats);
OPTITRACK_RTSP_API int optitrack_rtsp_get_frame(
    void* handle,
    uint8_t* destination,
    int destination_capacity,
    int* width,
    int* height,
    int* stride,
    uint64_t* sequence,
    int64_t* timestamp_ms,
    int* required_bytes);
OPTITRACK_RTSP_API void optitrack_rtsp_get_last_error(
    void* handle,
    char* error,
    int error_capacity);

#ifdef __cplusplus
}
#endif
