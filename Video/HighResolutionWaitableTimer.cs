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
    private readonly WaitHandle[] waitHandles;
    private readonly ILogger logger;
    private bool disposed;

    private HighResolutionWaitableTimer(SafeWaitHandle handle, ILogger logger)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        waitHandle = new WaitableTimerHandle(handle);
        waitHandles = new WaitHandle[2];
        waitHandles[0] = waitHandle;
    }

    internal static HighResolutionWaitableTimer? TryCreate(ILogger logger)
    {
        var timerHandle = CreateWaitableTimer(highResolution: true, out var error);
        if (timerHandle is not null)
        {
            logger.Debug("Using high-resolution waitable timer for paced output.");
            return new HighResolutionWaitableTimer(timerHandle, logger);
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

        SetTimer(dueTime);

        waitHandles[1] = token.WaitHandle;
        var signaled = WaitHandle.WaitAny(waitHandles);
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

    private static int setWaitableTimerExUnavailable;
    private static int setWaitableTimerExDowngradeLogged;

    private void SetTimer(LargeInteger dueTime)
    {
        if (Volatile.Read(ref setWaitableTimerExUnavailable) == 0)
        {
            if (NativeMethods.SetWaitableTimerEx(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorCallNotImplemented && error != NativeMethods.ErrorInvalidParameter)
            {
                throw new Win32Exception(error);
            }

            if (Interlocked.CompareExchange(ref setWaitableTimerExUnavailable, 1, 0) == 0)
            {
                if (Interlocked.CompareExchange(ref setWaitableTimerExDowngradeLogged, 1, 0) == 0)
                {
                    logger.Warning(
                        "SetWaitableTimerEx is unavailable (Win32 error {Error}); using SetWaitableTimer fallback for paced output.",
                        error);
                }
            }
            else
            {
                Interlocked.Exchange(ref setWaitableTimerExUnavailable, 1);
            }
        }

        if (!NativeMethods.SetWaitableTimer(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
        internal const int ErrorCallNotImplemented = 120;
        internal const int ErrorInvalidParameter = 87;

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimerEx(
            SafeWaitHandle hTimer,
            [In] ref LargeInteger lpDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            IntPtr wakeContext,
            uint tolerableDelay);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimer(SafeWaitHandle hTimer, [In] ref LargeInteger pDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelWaitableTimer(SafeWaitHandle hTimer);
    }
}
