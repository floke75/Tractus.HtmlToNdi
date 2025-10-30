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

    internal CompositorCaptureBridge(ILogger logger)
    {
        this.logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<CompositorCaptureBridge>();
    }

    internal event EventHandler<CapturedFrame>? FrameArrived;

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

    private static IntPtr ResolveBufferPointer(NativeCapturedFrame frame)
    {
        return frame.StorageType switch
        {
            NativeFrameStorageType.SharedTextureHandle => frame.SharedHandle,
            NativeFrameStorageType.SharedMemoryHandle => frame.SharedHandle,
            _ => frame.Buffer,
        };
    }

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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
    }

    private sealed class SafeCompositorCaptureHandle : SafeHandle
    {
        private SafeCompositorCaptureHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeMethods.cc_destroy_session(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCompositorCaptureConfig
    {
        public int Width;
        public int Height;
        public int FrameRateNumerator;
        public int FrameRateDenominator;
    }

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

    private enum NativeFrameStorageType
    {
        SystemMemory = 0,
        SharedTextureHandle = 1,
        SharedMemoryHandle = 2,
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FrameReadyCallback(ref NativeCapturedFrame frame, IntPtr userData);

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
