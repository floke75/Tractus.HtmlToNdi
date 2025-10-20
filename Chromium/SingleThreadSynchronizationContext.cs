using System.Collections.Concurrent;

namespace Tractus.HtmlToNdi.Chromium;
// https://github.com/cefsharp/CefSharp.MinimalExample/blob/master/CefSharp.MinimalExample.OffScreen/SingleThreadSynchronizationContext.cs

/// <summary>
/// Provides a synchronization context that executes all posted work on a single thread.
/// </summary>
public sealed class SingleThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> queue =
        [];

    /// <summary>
    /// Dispatches an asynchronous message to the synchronization context.
    /// </summary>
    /// <param name="d">The <see cref="SendOrPostCallback"/> delegate to call.</param>
    /// <param name="state">The object passed to the delegate.</param>
    public override void Post(SendOrPostCallback d, object state)
    {
        this.queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state));
    }

    /// <summary>
    /// Runs a message loop on the current thread, dispatching queued work items.
    /// </summary>
    public void RunOnCurrentThread()
    {
        while (this.queue.TryTake(out var workItem, Timeout.Infinite))
        {
            workItem.Key(workItem.Value);
        }
    }

    /// <summary>
    /// Signals that the message loop should be completed and no more items will be added.
    /// </summary>
    public void Complete()
    {
        this.queue.CompleteAdding();
    }
}
