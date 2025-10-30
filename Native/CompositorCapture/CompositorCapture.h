#pragma once

#include <cstdint>

class CefBrowserHost;
namespace viz
{
class FrameSinkVideoCapturer;
}

struct CompositorCaptureConfig
{
    int32_t width;
    int32_t height;
    int32_t frame_rate_numerator;
    int32_t frame_rate_denominator;
};

enum class CompositorFrameStorageType : uint32_t
{
    kSystemMemory = 0,
    kSharedTextureHandle = 1,
    kSharedMemoryHandle = 2,
};

struct CompositorCapturedFrame
{
    uint64_t frame_token;
    void* pixel_buffer;
    void* shared_handle;
    int32_t width;
    int32_t height;
    int32_t stride;
    int64_t monotonic_timestamp;
    int64_t timestamp_utc_microseconds;
    CompositorFrameStorageType storage_type;
};

using CompositorFrameCallback = void(__cdecl*)(const CompositorCapturedFrame* frame, void* user_data);

extern "C"
{
struct CompositorCaptureSession;

__declspec(dllexport) CompositorCaptureSession* cc_create_session(CefBrowserHost* host, const CompositorCaptureConfig* config, CompositorFrameCallback callback, void* user_data);
__declspec(dllexport) void cc_start_session(CompositorCaptureSession* session);
__declspec(dllexport) void cc_stop_session(CompositorCaptureSession* session);
__declspec(dllexport) void cc_release_frame(CompositorCaptureSession* session, uint64_t frame_token);
__declspec(dllexport) void cc_destroy_session(CompositorCaptureSession* session);
}
