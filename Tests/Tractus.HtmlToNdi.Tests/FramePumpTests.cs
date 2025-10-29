using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CefSharp.OffScreen;
using Serilog;
using Tractus.HtmlToNdi.Chromium;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FramePumpTests
{
    private static ChromiumWebBrowser CreateBrowserStub()
    {
        return (ChromiumWebBrowser)RuntimeHelpers.GetUninitializedObject(typeof(ChromiumWebBrowser));
    }

    private static ILogger CreateNullLogger()
    {
        return new LoggerConfiguration().WriteTo.Sink(new NullSink()).CreateLogger();
    }

    [Fact]
    public async Task OnDemandRequestsInvokeInvalidation()
    {
        var invocations = 0;
        using var pump = new FramePump(
            CreateBrowserStub(),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(50),
            CreateNullLogger(),
            FramePumpMode.OnDemand,
            cadenceAdaptationEnabled: false,
            (_, _, _) =>
            {
                Interlocked.Increment(ref invocations);
                return Task.CompletedTask;
            });

        pump.Start();

        await pump.RequestInvalidateAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task PausedPumpQueuesRequestsUntilResumed()
    {
        var invocations = 0;
        using var pump = new FramePump(
            CreateBrowserStub(),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(50),
            CreateNullLogger(),
            FramePumpMode.OnDemand,
            cadenceAdaptationEnabled: false,
            (_, _, _) =>
            {
                Interlocked.Increment(ref invocations);
                return Task.CompletedTask;
            });

        pump.Start();
        pump.Pause();

        var requestTask = pump.RequestInvalidateAsync();
        var completedEarly = await Task.WhenAny(requestTask, Task.Delay(100));
        Assert.NotSame(requestTask, completedEarly);

        pump.Resume();
        await requestTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task WatchdogTriggersInvalidateAfterIdle()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new FramePump(
            CreateBrowserStub(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(40),
            CreateNullLogger(),
            FramePumpMode.OnDemand,
            cadenceAdaptationEnabled: false,
            (_, _, _) =>
            {
                tcs.TrySetResult(1);
                return Task.CompletedTask;
            });

        pump.Start();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CadenceAlignmentDelaysOnDemandRequests()
    {
        var stopwatch = Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new FramePump(
            CreateBrowserStub(),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(200),
            CreateNullLogger(),
            FramePumpMode.OnDemand,
            cadenceAdaptationEnabled: true,
            (_, _, _) =>
            {
                tcs.TrySetResult(stopwatch.Elapsed);
                return Task.CompletedTask;
            });

        pump.Start();
        pump.UpdateCadenceAlignment(1d);

        await pump.RequestInvalidateAsync().WaitAsync(TimeSpan.FromSeconds(1));
        var elapsed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(15), $"Expected at least 15ms delay, saw {elapsed.TotalMilliseconds:F2}ms");
    }
}
