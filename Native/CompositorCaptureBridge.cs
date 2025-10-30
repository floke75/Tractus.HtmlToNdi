using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CefSharp;
using Serilog;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Native;

/// <summary>
/// Managed wrapper around the native compositor capture helper.
/// </summary>
internal sealed class CompositorCaptureBridge : IDisposable
{
    private readonly ILogger logger;
    private SafeCompositorCaptureHandle? sessionHandle;
    private GCHandle selfHandle;
    private FrameReadyCallback? frameCallback;
    private IntPtr hostPtr;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositorCaptureBridge"/> class.
    /// </summary>
    /// <param name="logger">The logger used for diagnostics and error reporting.</param>
    internal CompositorCaptureBridge(ILogger logger)
    {
        this.logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<CompositorCaptureBridge>();
    }

    /// <summary>
    /// Occurs when the native compositor helper has produced a frame for consumption by the video pipeline.
    /// </summary>
    internal event EventHandler<CapturedFrame>? FrameArrived;

    /// <summary>
    /// Attempts to start a compositor capture session that delivers frames via the supplied callback.
    /// </summary>
    /// <param name="host">The Chromium browser host that owns the compositor.</param>
    /// <param name="width">The expected frame width.</param>
    /// <param name="height">The expected frame height.</param>
    /// <param name="frameRate">The target frame rate advertised to the compositor helper.</param>
    /// <param name="error">When this method returns <c>false</c>, contains the error message describing why start-up failed.</param>
    /// <returns><c>true</c> when the compositor capture session was created and started; otherwise <c>false</c>.</returns>
    internal bool TryStart(IBrowserHost host, int width, int height, FrameRate frameRate, out string? error)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (sessionHandle is not null && !sessionHandle.IsInvalid)
        {
            error = "Compositor capture session already active.";
            return false;
        }

        var config = new NativeCompositorCaptureConfig
        {
            Width = width,
            Height = height,
            FrameRateNumerator = frameRate.Numerator,
            FrameRateDenominator = frameRate.Denominator,
        };

        try
        {
            hostPtr = Marshal.GetIUnknownForObject(host);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to marshal browser host for compositor capture");
            error = ex.Message;
            return false;
        }

        frameCallback = OnNativeFrame;
        selfHandle = GCHandle.Alloc(this);

        SafeCompositorCaptureHandle? handle = null;
        try
        {
            handle = NativeMethods.cc_create_session(hostPtr, ref config, frameCallback, GCHandle.ToIntPtr(selfHandle));
        }
        catch (DllNotFoundException ex)
        {
            logger.Warning(ex, "Compositor capture helper DLL was not found");
            CleanupCallbackState();
            error = ex.Message;
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.Warning(ex, "Compositor capture helper DLL is missing expected entry points");
            CleanupCallbackState();
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Compositor capture session creation failed");
            CleanupCallbackState();
            error = ex.Message;
            return false;
        }

        if (handle is null || handle.IsInvalid)
        {
            logger.Warning("Native compositor capture session creation returned an invalid handle");
            CleanupCallbackState();
            error = "Native session was not created.";
            return false;
        }

        sessionHandle = handle;
        try
        {
            NativeMethods.cc_start_session(handle);
            error = null;
            logger.Information("Compositor capture session started (size={Width}x{Height}, rate={Rate})", width, height, frameRate);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed to start compositor capture session");
            Stop();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Stops the compositor capture session and releases any pinned managed resources.
    /// </summary>
    internal void Stop()
    {
        var handle = sessionHandle;
        if (handle is null)
        {
            CleanupCallbackState();
            return;
        }

        try
        {
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                if (added)
                {
                    NativeMethods.cc_stop_session(handle.DangerousGetHandle());
                }
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }
        }
        catch (DllNotFoundException)
        {
            // Ignore: the helper DLL is missing, which was already logged during start.
        }
        catch (EntryPointNotFoundException)
        {
            // Ignore: handled during start.
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Stopping compositor capture session raised an exception");
        }
        finally
        {
            handle.Dispose();
            sessionHandle = null;
            CleanupCallbackState();
            logger.Information("Compositor capture session stopped");
        }
    }

    /// <summary>
    /// Releases pinned delegates, GC handles, and browser host references created during session start-up.
    /// </summary>
    private void CleanupCallbackState()
    {
        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }

        frameCallback = null;

        if (hostPtr != IntPtr.Zero)
        {
            try
            {
                Marshal.Release(hostPtr);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to release browser host pointer");
            }
            finally
            {
                hostPtr = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Static callback invoked by the native helper whenever a new frame becomes available.
    /// </summary>
    /// <param name="frame">The frame provided by the native capturer.</param>
    /// <param name="userData">Opaque pointer used to recover the managed <see cref="CompositorCaptureBridge"/> instance.</param>
    private static void OnNativeFrame(ref NativeCapturedFrame frame, IntPtr userData)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        GCHandle handle;
        try
        {
            handle = GCHandle.FromIntPtr(userData);
        }
        catch (Exception)
        {
            return;
        }

        if (!handle.IsAllocated || handle.Target is not CompositorCaptureBridge bridge)
        {
            return;
        }

        bridge.DispatchFrame(frame);
    }

    /// <summary>
    /// Translates the native frame representation into a managed <see cref="CapturedFrame"/> and raises <see cref="FrameArrived"/>.
    /// </summary>
    /// <param name="frame">The native frame payload.</param>
    private void DispatchFrame(NativeCapturedFrame frame)
    {
        var session = sessionHandle;
        if (session is null || session.IsInvalid)
        {
            return;
        }

        DateTime timestampUtc;
        try
        {
            var ticks = frame.TimestampMicrosecondsUtc <= 0 ? 0 : checked(frame.TimestampMicrosecondsUtc * 10);
            timestampUtc = DateTime.UnixEpoch.AddTicks(ticks);
        }
        catch (Exception)
        {
            timestampUtc = DateTime.UtcNow;
        }

        var monotonicTicks = frame.MonotonicTimestamp != 0 ? frame.MonotonicTimestamp : Stopwatch.GetTimestamp();
        var bufferPointer = ResolveBufferPointer(frame);
        var releaseAction = CreateReleaseAction(session, frame.FrameToken);
        var storageKind = frame.StorageType switch
        {
            NativeFrameStorageType.SharedTextureHandle => CapturedFrameStorageKind.SharedTextureHandle,
            NativeFrameStorageType.SharedMemoryHandle => CapturedFrameStorageKind.SharedMemoryHandle,
            _ => CapturedFrameStorageKind.CpuMemory,
        };
        var capturedFrame = new CapturedFrame(
            bufferPointer,
            frame.Width,
            frame.Height,
            frame.Stride,
            monotonicTicks,
            timestampUtc,
            releaseAction,
            storageKind);

        var handlers = FrameArrived;
        if (handlers is null)
        {
            capturedFrame.Dispose();
            return;
        }

        try
        {
            handlers.Invoke(this, capturedFrame);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unhandled exception while delivering compositor frame");
            capturedFrame.Dispose();
        }
    }

    /// <summary>
    /// Determines the appropriate pointer to expose to managed consumers based on the storage type.
    /// </summary>
    /// <param name="frame">The native frame supplied by the helper.</param>
    /// <returns>The pointer that represents the pixel payload for the frame.</returns>
    private static IntPtr ResolveBufferPointer(NativeCapturedFrame frame)
    {
        return frame.StorageType switch
        {
            NativeFrameStorageType.SharedTextureHandle => frame.SharedHandle,
            NativeFrameStorageType.SharedMemoryHandle => frame.SharedHandle,
            _ => frame.Buffer,
        };
    }

    /// <summary>
    /// Creates an action that returns ownership of a captured frame back to the native compositor session.
    /// </summary>
    /// <param name="handle">The active session handle.</param>
    /// <param name="token">The token that uniquely identifies the native frame.</param>
    /// <returns>An <see cref="Action"/> that releases the native resources when invoked.</returns>
    private static Action CreateReleaseAction(SafeCompositorCaptureHandle handle, ulong token)
    {
        return () =>
        {
            if (handle.IsInvalid)
            {
                return;
            }

            try
            {
                var added = false;
                try
                {
                    handle.DangerousAddRef(ref added);
                    if (added)
                    {
                        NativeMethods.cc_release_frame(handle.DangerousGetHandle(), token);
                    }
                }
                finally
                {
                    if (added)
                    {
                        handle.DangerousRelease();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        };
    }

    /// <summary>
    /// Releases resources held by the <see cref="CompositorCaptureBridge"/>.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
    }

    /// <summary>
    /// Safe handle wrapper for the native compositor capture session.
    /// </summary>
    private sealed class SafeCompositorCaptureHandle : SafeHandle
    {
        private SafeCompositorCaptureHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        /// <inheritdoc />
        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeMethods.cc_destroy_session(handle);
            return true;
        }
    }

    /// <summary>
    /// Native configuration passed to the compositor helper when creating a session.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCompositorCaptureConfig
    {
        public int Width;
        public int Height;
        public int FrameRateNumerator;
        public int FrameRateDenominator;
    }

    /// <summary>
    /// Native frame descriptor supplied by the compositor helper callback.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCapturedFrame
    {
        public ulong FrameToken;
        public IntPtr Buffer;
        public IntPtr SharedHandle;
        public int Width;
        public int Height;
        public int Stride;
        public long MonotonicTimestamp;
        public long TimestampMicrosecondsUtc;
        public NativeFrameStorageType StorageType;
    }

    /// <summary>
    /// Storage hints surfaced by the native helper to describe each frame.
    /// </summary>
    private enum NativeFrameStorageType
    {
        SystemMemory = 0,
        SharedTextureHandle = 1,
        SharedMemoryHandle = 2,
    }

    /// <summary>
    /// Delegate signature used by the native helper to deliver frames.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FrameReadyCallback(ref NativeCapturedFrame frame, IntPtr userData);

    /// <summary>
    /// P/Invoke declarations that bridge to the native compositor helper.
    /// </summary>
    private static class NativeMethods
    {
        [DllImport("CompositorCapture", EntryPoint = "cc_create_session", CallingConvention = CallingConvention.Cdecl)]
        internal static extern SafeCompositorCaptureHandle cc_create_session(IntPtr browserHost, ref NativeCompositorCaptureConfig config, FrameReadyCallback callback, IntPtr userData);

        [DllImport("CompositorCapture", EntryPoint = "cc_start_session", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cc_start_session(SafeCompositorCaptureHandle session);

        [DllImport("CompositorCapture", EntryPoint = "cc_stop_session", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cc_stop_session(IntPtr session);

        [DllImport("CompositorCapture", EntryPoint = "cc_release_frame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cc_release_frame(IntPtr session, ulong frameToken);

        [DllImport("CompositorCapture", EntryPoint = "cc_destroy_session", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cc_destroy_session(IntPtr session);
    }
}
