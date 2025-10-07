using System.Runtime.InteropServices;
using NewTek.NDI;
using Serilog;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class NdiVideoPipelineTests
{
    private sealed class FakeSender : INdiVideoSender
    {
        public List<byte> PayloadMarkers { get; } = new();

        public List<IntPtr> SentPointers { get; } = new();

        public void Send(ref NDIlib.video_frame_v2_t frame)
        {
            SentPointers.Add(frame.p_data);
            if (frame.p_data != IntPtr.Zero)
            {
                PayloadMarkers.Add(Marshal.ReadByte(frame.p_data));
            }
            else
            {
                PayloadMarkers.Add(0);
            }
        }
    }

    private static CapturedFrame CreateFrame(byte marker, out IntPtr allocated)
    {
        var buffer = Marshal.AllocHGlobal(4);
        var pattern = new byte[4];
        Array.Fill(pattern, marker);
        Marshal.Copy(pattern, 0, buffer, pattern.Length);
        allocated = buffer;
        return new CapturedFrame(buffer, 1, 1, 4);
    }

    private static void EnqueueFrame(NdiVideoPipeline pipeline, byte marker)
    {
        var frame = CreateFrame(marker, out var allocated);
        try
        {
            pipeline.HandleFrame(frame);
        }
        finally
        {
            Marshal.FreeHGlobal(allocated);
        }
    }

    private static NdiVideoPipeline CreatePipeline(FakeSender sender)
    {
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            TelemetryInterval = TimeSpan.FromHours(1)
        };

        var logger = new LoggerConfiguration().CreateLogger();
        return new NdiVideoPipeline(sender, new FrameRate(60, 1), options, logger);
    }

    [Fact]
    public void BufferedPipelineWarmsUpBeforeSending()
    {
        var sender = new FakeSender();
        using var pipeline = CreatePipeline(sender);

        pipeline.ProcessPacingTick();
        Assert.Empty(sender.PayloadMarkers);
        Assert.False(pipeline.IsBufferPrimed);

        EnqueueFrame(pipeline, 0x11);
        pipeline.ProcessPacingTick();
        Assert.Empty(sender.PayloadMarkers);
        Assert.False(pipeline.IsBufferPrimed);

        EnqueueFrame(pipeline, 0x22);
        pipeline.ProcessPacingTick();

        Assert.True(pipeline.IsBufferPrimed);
        Assert.Equal(new byte[] { 0x11 }, sender.PayloadMarkers);

        EnqueueFrame(pipeline, 0x33);
        pipeline.ProcessPacingTick();

        Assert.Equal(new byte[] { 0x11, 0x22 }, sender.PayloadMarkers);
        Assert.True(pipeline.IsBufferPrimed);
    }

    [Fact]
    public void BufferedPipelineRewarmsAndCountsUnderruns()
    {
        var sender = new FakeSender();
        using var pipeline = CreatePipeline(sender);

        pipeline.ProcessPacingTick();
        EnqueueFrame(pipeline, 0x10);
        pipeline.ProcessPacingTick();
        EnqueueFrame(pipeline, 0x20);
        pipeline.ProcessPacingTick();
        EnqueueFrame(pipeline, 0x30);
        pipeline.ProcessPacingTick();

        Assert.Equal(new byte[] { 0x10, 0x20 }, sender.PayloadMarkers);
        Assert.True(pipeline.IsBufferPrimed);
        Assert.Equal(0, pipeline.UnderrunCount);

        pipeline.ProcessPacingTick();
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, sender.PayloadMarkers);
        Assert.True(pipeline.IsBufferPrimed);

        pipeline.ProcessPacingTick();
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x30 }, sender.PayloadMarkers);
        Assert.False(pipeline.IsBufferPrimed);
        Assert.Equal(1, pipeline.UnderrunCount);
        Assert.Equal(sender.SentPointers[^1], sender.SentPointers[^2]);

        EnqueueFrame(pipeline, 0x40);
        pipeline.ProcessPacingTick();
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x30, 0x30 }, sender.PayloadMarkers);
        Assert.False(pipeline.IsBufferPrimed);
        Assert.Equal(1, pipeline.UnderrunCount);

        EnqueueFrame(pipeline, 0x50);
        pipeline.ProcessPacingTick();
        Assert.True(pipeline.IsBufferPrimed);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x30, 0x30, 0x40 }, sender.PayloadMarkers);
        Assert.Equal(1, pipeline.UnderrunCount);
    }
}
