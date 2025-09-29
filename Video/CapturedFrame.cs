namespace Tractus.HtmlToNdi.Video;

internal readonly struct CapturedFrame
{
    public CapturedFrame(IntPtr buffer, int width, int height, int stride)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public IntPtr Buffer { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int SizeInBytes => Height * Stride;
}
