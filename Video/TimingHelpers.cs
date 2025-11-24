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
    // delay falls under this value we fall back to short sleeps so we do not burn
    // an entire frame interval spinning when a high-resolution timer is absent.
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
                // Use short sleeps to avoid chewing CPU for long intervals when
                // the platform timer has coarse resolution. `Task.Delay` can still
                // overshoot, but spreading the wait across multiple 1ms sleeps is
                // less disruptive than a tight spin loop.
                try
                {
                    Task.Delay(TimeSpan.FromMilliseconds(1), token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }

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
