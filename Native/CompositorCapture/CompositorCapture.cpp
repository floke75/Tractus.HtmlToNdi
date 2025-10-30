#include "CompositorCapture.h"

#include <atomic>
#include <chrono>
#include <memory>
#include <thread>
#include <vector>

#include "include/cef_browser.h"

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
/// Implements compositor capture orchestration by owning the viz capturer and dispatch logic.
/// </summary>
class CompositorCaptureSessionImpl
{
public:
    /// <summary>
    /// Creates a new compositor capture session implementation.
    /// </summary>
    CompositorCaptureSessionImpl(CefBrowserHost* /*browser_host*/, const CompositorCaptureConfig& config, CompositorFrameCallback callback, void* user_data)
        : config_(config), callback_(callback), user_data_(user_data)
    {
        capturer_ = CreateCapturer();
    }

    /// <summary>
    /// Stops the fallback capture loop when the session is destroyed.
    /// </summary>
    ~CompositorCaptureSessionImpl()
    {
        StopFallbackLoop();
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
#else
        StartFallbackLoop();
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
#else
        StopFallbackLoop();
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

    void StartFallbackLoop()
    {
        if (!callback_)
        {
            return;
        }

        bool expected = false;
        if (!running_.compare_exchange_strong(expected, true))
        {
            return;
        }

        const auto bufferSize = CalculateBufferSize();
        staging_buffer_.assign(bufferSize, 0u);
        capture_thread_ = std::thread([this]() { RunFallbackLoop(); });
    }

    void StopFallbackLoop()
    {
        if (!running_.exchange(false))
        {
            return;
        }

        if (capture_thread_.joinable())
        {
            capture_thread_.join();
        }
    }

    size_t CalculateBufferSize() const
    {
        if (config_.width <= 0 || config_.height <= 0)
        {
            return 0;
        }

        return static_cast<size_t>(config_.width) * static_cast<size_t>(config_.height) * 4u;
    }

    int32_t CalculateStride() const
    {
        if (config_.width <= 0)
        {
            return 0;
        }

        return config_.width * 4;
    }

    static std::chrono::microseconds CalculateFrameInterval(const CompositorCaptureConfig& config)
    {
        if (config.frame_rate_numerator <= 0 || config.frame_rate_denominator <= 0)
        {
            return std::chrono::microseconds(16667);
        }

        const double period_seconds = static_cast<double>(config.frame_rate_denominator) /
                                       static_cast<double>(config.frame_rate_numerator);
        auto microseconds = static_cast<int64_t>(period_seconds * 1'000'000.0);
        if (microseconds <= 0)
        {
            microseconds = 16667;
        }

        return std::chrono::microseconds(microseconds);
    }

    void RunFallbackLoop()
    {
        const auto interval = CalculateFrameInterval(config_);
        const auto stride = CalculateStride();

        while (running_.load())
        {
            const auto monotonic = std::chrono::steady_clock::now();
            const auto system = std::chrono::system_clock::now();

            CompositorCapturedFrame frame{};
            frame.frame_token = ++next_frame_token_;
            frame.pixel_buffer = staging_buffer_.empty() ? nullptr : staging_buffer_.data();
            frame.shared_handle = nullptr;
            frame.width = config_.width;
            frame.height = config_.height;
            frame.stride = stride;
            frame.monotonic_timestamp = std::chrono::duration_cast<std::chrono::microseconds>(monotonic.time_since_epoch()).count();
            frame.timestamp_utc_microseconds = std::chrono::duration_cast<std::chrono::microseconds>(system.time_since_epoch()).count();
            frame.storage_type = CompositorFrameStorageType::kSystemMemory;

            if (callback_)
            {
                callback_(&frame, user_data_);
            }
            else
            {
                running_.store(false);
                break;
            }

            const auto next_fire = monotonic + interval;
            std::this_thread::sleep_until(next_fire);
        }
    }

    CompositorCaptureConfig config_;
    CompositorFrameCallback callback_;
    void* user_data_;
    std::unique_ptr<viz::FrameSinkVideoCapturer> capturer_;
    bool started_{false};
    std::atomic<bool> running_{false};
    std::thread capture_thread_;
    std::vector<uint8_t> staging_buffer_;
    uint64_t next_frame_token_{0};
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
