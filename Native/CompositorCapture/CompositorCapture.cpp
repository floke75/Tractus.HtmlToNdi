#include "CompositorCapture.h"

#include <memory>

#include "include/cef_browser.h"
#include "include/wrapper/cef_helpers.h"

#if __has_include("components/viz/service/frame_sinks/frame_sink_video_capturer.h")
#include "components/viz/service/frame_sinks/frame_sink_video_capturer.h"
#define TRACTUS_HAS_VIZ_CAPTURER 1
#else
#define TRACTUS_HAS_VIZ_CAPTURER 0
namespace viz
{
class FrameSinkVideoCapturer
{
public:
    FrameSinkVideoCapturer() = default;
    ~FrameSinkVideoCapturer() = default;
    void Start() {}
    void Stop() {}
};
} // namespace viz
#endif

namespace
{
/// <summary>
/// Implements compositor capture orchestration by owning the Chromium host and viz capturer.
/// </summary>
class CompositorCaptureSessionImpl
{
public:
    /// <summary>
    /// Creates a new compositor capture session implementation.
    /// </summary>
    CompositorCaptureSessionImpl(CefBrowserHost* browser_host, const CompositorCaptureConfig& config, CompositorFrameCallback callback, void* user_data)
        : host_(browser_host), config_(config), callback_(callback), user_data_(user_data)
    {
        if (host_)
        {
            host_->SetAutoBeginFrameEnabled(false);
        }

        capturer_ = CreateCapturer();
    }

    /// <summary>
    /// Ensures Chromium resumes its default begin-frame behaviour when the session is destroyed.
    /// </summary>
    ~CompositorCaptureSessionImpl()
    {
        if (host_)
        {
            host_->SetAutoBeginFrameEnabled(true);
        }
    }

    /// <summary>
    /// Starts the compositor capture flow and primes the viz capturer when available.
    /// </summary>
    void Start()
    {
#if TRACTUS_HAS_VIZ_CAPTURER
        if (capturer_)
        {
            capturer_->Start();
        }
#endif
        started_ = true;
    }

    /// <summary>
    /// Stops the compositor capture flow and notifies the viz capturer to halt production.
    /// </summary>
    void Stop()
    {
#if TRACTUS_HAS_VIZ_CAPTURER
        if (capturer_)
        {
            capturer_->Stop();
        }
#endif
        started_ = false;
    }

    /// <summary>
    /// Releases compositor frame resources once managed consumers signal completion.
    /// </summary>
    void ReleaseFrame(uint64_t)
    {
        // Placeholder for native frame lifetime management.
    }

private:
    /// <summary>
    /// Creates a viz capturer instance when Chromium exports the required headers.
    /// </summary>
    static std::unique_ptr<viz::FrameSinkVideoCapturer> CreateCapturer()
    {
#if TRACTUS_HAS_VIZ_CAPTURER
        return std::make_unique<viz::FrameSinkVideoCapturer>();
#else
        return std::make_unique<viz::FrameSinkVideoCapturer>();
#endif
    }

    CefRefPtr<CefBrowserHost> host_;
    CompositorCaptureConfig config_;
    CompositorFrameCallback callback_;
    void* user_data_;
    std::unique_ptr<viz::FrameSinkVideoCapturer> capturer_;
    bool started_{false};
};
} // namespace

extern "C"
{
struct CompositorCaptureSession
{
    explicit CompositorCaptureSession(CompositorCaptureSessionImpl* impl)
        : impl_(impl)
    {
    }

    ~CompositorCaptureSession()
    {
        delete impl_;
    }

    CompositorCaptureSessionImpl* impl_;
};

CompositorCaptureSession* cc_create_session(CefBrowserHost* host, const CompositorCaptureConfig* config, CompositorFrameCallback callback, void* user_data)
{
    if (host == nullptr || config == nullptr || callback == nullptr)
    {
        return nullptr;
    }

    auto impl = new CompositorCaptureSessionImpl(host, *config, callback, user_data);
    return new CompositorCaptureSession(impl);
}

void cc_start_session(CompositorCaptureSession* session)
{
    if (session == nullptr || session->impl_ == nullptr)
    {
        return;
    }

    session->impl_->Start();
}

void cc_stop_session(CompositorCaptureSession* session)
{
    if (session == nullptr || session->impl_ == nullptr)
    {
        return;
    }

    session->impl_->Stop();
}

void cc_release_frame(CompositorCaptureSession* session, uint64_t frame_token)
{
    if (session == nullptr || session->impl_ == nullptr)
    {
        return;
    }

    session->impl_->ReleaseFrame(frame_token);
}

void cc_destroy_session(CompositorCaptureSession* session)
{
    delete session;
}
}
