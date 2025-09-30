using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FramePacerTests
{
    [Fact]
    public async Task RepeatsLastFrameWhenProducerIsQuiet()
    {
        var fakeTime = new FakeTimeProvider();
        var buffer = new FrameRingBuffer(3);
        var sender = new TestVideoSender();
        var pacer = new FramePacer(buffer, sender, 30.0, fakeTime);

        buffer.Write(CreateFrame(sequence: 1));
        pacer.Start();

        fakeTime.Advance(TimeSpan.FromMilliseconds(33.366));
        await SpinWaitAsync(() => sender.Count >= 1);

        fakeTime.Advance(TimeSpan.FromMilliseconds(33.366));
        await SpinWaitAsync(() => sender.Count >= 2);

        var snapshot = sender.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.False(snapshot[0].IsRepeat);
        Assert.True(snapshot[1].IsRepeat);

        await pacer.DisposeAsync();
    }

    [Fact]
    public async Task DropsOlderFramesWhenProducerOverruns()
    {
        var fakeTime = new FakeTimeProvider();
        var buffer = new FrameRingBuffer(2);
        var sender = new TestVideoSender();
        var pacer = new FramePacer(buffer, sender, 10.0, fakeTime);

        pacer.Start();

        buffer.Write(CreateFrame(sequence: 1));
        buffer.Write(CreateFrame(sequence: 2));
        buffer.Write(CreateFrame(sequence: 3));

        fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        await SpinWaitAsync(() => sender.Count >= 1);

        var snapshot = sender.Snapshot();
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(2, snapshot[0].DroppedFrames);
        Assert.False(snapshot[0].IsRepeat);

        await pacer.DisposeAsync();
    }

    private static VideoFrameData CreateFrame(int sequence)
    {
        var pixels = new byte[4];
        return new VideoFrameData(1, 1, 4, pixels, sequence, DateTime.UtcNow);
    }

    private static async Task SpinWaitAsync(Func<bool> predicate, int timeoutMs = 1000)
    {
        var start = Environment.TickCount64;
        while (!predicate())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class TestVideoSender : IVideoFrameSender
    {
        private readonly List<SentFrame> sent = new();
        private readonly object gate = new();

        public int Count
        {
            get
            {
                lock (this.gate)
                {
                    return this.sent.Count;
                }
            }
        }

        public IReadOnlyList<SentFrame> Snapshot()
        {
            lock (this.gate)
            {
                return this.sent.ToArray();
            }
        }

        public void Send(VideoFrameData frame, bool isRepeat, long droppedFrames, TimeSpan frameInterval)
        {
            lock (this.gate)
            {
                this.sent.Add(new SentFrame(frame, isRepeat, droppedFrames));
            }
        }
    }

    public sealed record SentFrame(VideoFrameData Frame, bool IsRepeat, long DroppedFrames);
}
