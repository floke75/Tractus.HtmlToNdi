using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class SystemFrameClock : IFrameClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayUntilAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var delay = timestamp - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        return Task.Delay(delay, cancellationToken);
    }
}
