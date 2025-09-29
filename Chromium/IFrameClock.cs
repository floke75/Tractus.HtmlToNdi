using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tractus.HtmlToNdi.Chromium;

internal interface IFrameClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayUntilAsync(DateTimeOffset timestamp, CancellationToken cancellationToken);
}
