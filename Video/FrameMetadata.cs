using System;

namespace Tractus.HtmlToNdi.Video;

public readonly struct FrameMetadata
{
    public FrameMetadata(int width, int height, int stride, int bufferLength, DateTime capturedAtUtc)
    {
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.BufferLength = bufferLength;
        this.CapturedAtUtc = capturedAtUtc;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int BufferLength { get; }
    public DateTime CapturedAtUtc { get; }
}
