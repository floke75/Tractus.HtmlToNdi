namespace Tractus.HtmlToNdi.Chromium;

//https://github.com/cefsharp/CefSharp.MinimalExample/blob/master/CefSharp.MinimalExample.OffScreen/AsyncContext.cs

/// <summary>
/// Provides a single-threaded synchronization context to run asynchronous operations,
/// which is a common requirement for UI frameworks like CefSharp.
/// </summary>
public static class AsyncContext
{
    /// <summary>
    /// Executes an asynchronous task on a dedicated single-threaded synchronization context.
    /// </summary>
    /// <param name="func">The asynchronous function to execute.</param>
    public static void Run(Func<Task> func)
    {
        var prevCtx = SynchronizationContext.Current;

        try
        {
            var syncCtx = new SingleThreadSynchronizationContext();

            SynchronizationContext.SetSynchronizationContext(syncCtx);

            var t = func();

            t.ContinueWith(delegate
            {
                syncCtx.Complete();
            }, TaskScheduler.Default);

            syncCtx.RunOnCurrentThread();

            t.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevCtx);
        }
    }
}
