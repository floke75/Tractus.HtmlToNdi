using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

internal sealed class HighResolutionWaitableTimer : IDisposable
{
    private static int highResolutionUnavailableLogged;

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    private readonly SafeWaitHandle handle;
    private readonly WaitableTimerHandle waitHandle;
    private bool disposed;

    private HighResolutionWaitableTimer(SafeWaitHandle handle)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        waitHandle = new WaitableTimerHandle(handle);
    }

    internal static HighResolutionWaitableTimer? TryCreate(ILogger logger)
    {
        var timerHandle = CreateWaitableTimer(highResolution: true, out var error);
        if (timerHandle is not null)
        {
            logger.Debug("Using high-resolution waitable timer for paced output.");
            return new HighResolutionWaitableTimer(timerHandle);
        }

        if (Interlocked.CompareExchange(ref highResolutionUnavailableLogged, 1, 0) == 0)
        {
            if (error != 0)
            {
                logger.Warning(
                    "High-resolution waitable timers are unavailable (Win32 error {Error}); falling back to Stopwatch pacing.",
                    error);
            }
            else
            {
                logger.Warning("High-resolution waitable timers are unavailable; falling back to Stopwatch pacing.");
            }
        }

        return null;
    }

    internal void Wait(TimeSpan duration, CancellationToken token)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var ticks = duration.Ticks;
        if (ticks <= 0)
        {
            return;
        }

        var dueTime = new LargeInteger
        {
            QuadPart = -ticks,
        };

        if (!NativeMethods.SetWaitableTimer(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var signaled = WaitHandle.WaitAny(new WaitHandle[] { waitHandle, token.WaitHandle });
        if (signaled == WaitHandle.WaitTimeout)
        {
            return;
        }

        if (signaled == 1)
        {
            NativeMethods.CancelWaitableTimer(handle);
            token.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        NativeMethods.CancelWaitableTimer(handle);
        waitHandle.Dispose();
    }

    private static SafeWaitHandle? CreateWaitableTimer(bool highResolution, out int error)
    {
        var flags = highResolution ? CreateWaitableTimerHighResolution : 0u;
        var handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, flags, TimerAllAccess);
        if (handle is not null && !handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastWin32Error();
        handle?.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LargeInteger
    {
        public long QuadPart;
    }

    private sealed class WaitableTimerHandle : WaitHandle
    {
        internal WaitableTimerHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimer(SafeWaitHandle hTimer, [In] ref LargeInteger pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelWaitableTimer(SafeWaitHandle hTimer);
    }
}
