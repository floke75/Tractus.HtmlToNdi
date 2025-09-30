using System;

namespace Tractus.HtmlToNdi.Video;

public sealed class VideoFrameData
{
    public VideoFrameData(int width, int height, int stride, byte[] pixels, long sequence, DateTime capturedAtUtc)
    {
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.Pixels = pixels;
        this.Sequence = sequence;
        this.CapturedAtUtc = capturedAtUtc;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    public long Sequence { get; }

    public DateTime CapturedAtUtc { get; }
}
