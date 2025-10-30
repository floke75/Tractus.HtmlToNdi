#pragma once

#include <cstdint>

class CefBrowserHost;
namespace viz
{
class FrameSinkVideoCapturer;
}

/// <summary>
/// Configuration supplied when creating a compositor capture session.
/// </summary>
struct CompositorCaptureConfig
{
    int32_t width;
    int32_t height;
    int32_t frame_rate_numerator;
    int32_t frame_rate_denominator;
};

/// <summary>
/// Describes how native compositor frames surface their pixel payload.
/// </summary>
enum class CompositorFrameStorageType : uint32_t
{
    kSystemMemory = 0,
    kSharedTextureHandle = 1,
    kSharedMemoryHandle = 2,
};

/// <summary>
/// Native representation of a captured compositor frame.
/// </summary>
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

/// <summary>
/// Callback signature used by the compositor capture helper to surface frames to managed callers.
/// </summary>
using CompositorFrameCallback = void(__cdecl*)(const CompositorCapturedFrame* frame, void* user_data);

extern "C"
{
struct CompositorCaptureSession;

/// <summary>
/// Creates a compositor capture session for the specified browser host and configuration.
/// </summary>
/// <param name="host">The browser host that owns the compositor.</param>
/// <param name="config">Requested capture configuration (size and cadence).</param>
/// <param name="callback">Callback invoked for each captured frame.</param>
/// <param name="user_data">Opaque pointer forwarded with each callback invocation.</param>
/// <returns>A session handle that must be destroyed with <c>cc_destroy_session</c>.</returns>
__declspec(dllexport) CompositorCaptureSession* cc_create_session(CefBrowserHost* host, const CompositorCaptureConfig* config, CompositorFrameCallback callback, void* user_data);
/// <summary>
/// Begins compositor capture for the supplied session.
/// </summary>
__declspec(dllexport) void cc_start_session(CompositorCaptureSession* session);
/// <summary>
/// Stops compositor capture for the supplied session.
/// </summary>
__declspec(dllexport) void cc_stop_session(CompositorCaptureSession* session);
/// <summary>
/// Returns a frame to the native compositor once managed consumers have finished processing it.
/// </summary>
__declspec(dllexport) void cc_release_frame(CompositorCaptureSession* session, uint64_t frame_token);
/// <summary>
/// Destroys a compositor capture session and releases native resources.
/// </summary>
__declspec(dllexport) void cc_destroy_session(CompositorCaptureSession* session);
}
