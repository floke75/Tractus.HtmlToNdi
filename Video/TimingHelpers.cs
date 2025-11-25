using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tractus.HtmlToNdi.Video;

internal static class TimingHelpers
{
    private static readonly TimeSpan BusyWaitThreshold = TimeSpan.FromMilliseconds(0.5);

    // Threshold below which we avoid Task.Delay on systems without high-res timers
    // to prevent oversleeping due to the 15ms system timer quantum. If the coarse
    // delay falls under this value we busy-wait to preserve deadline precision even
    // though it costs CPU when a high-resolution timer is absent.
    private static readonly TimeSpan SpinFallbackThreshold = TimeSpan.FromMilliseconds(10);

    public static void WaitUntil(
        Stopwatch clock,
        TimeSpan deadline,
        CancellationToken token,
        HighResolutionWaitableTimer? highResolutionTimer)
    {
        while (!token.IsCancellationRequested)
        {
            var remaining = deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining <= BusyWaitThreshold)
            {
                while (deadline > clock.Elapsed)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.SpinWait(64);
                }

                return;
            }

            var coarse = remaining - BusyWaitThreshold;
            if (coarse <= TimeSpan.Zero)
            {
                continue;
            }

            if (highResolutionTimer is not null)
            {
                try
                {
                    highResolutionTimer.Wait(coarse, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (coarse < SpinFallbackThreshold)
            {
                // Skip sleeping on coarse timers to avoid overshooting short waits.
                // The loop will fall back to a pure spin, sacrificing CPU for
                // deadline precision when high-resolution timers are unavailable.
                continue;
            }

            try
            {
                Task.Delay(coarse, token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
