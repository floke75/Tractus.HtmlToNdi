namespace Tractus.HtmlToNdi.FramePacing;

/// <summary>
/// Represents a frame produced by the Chromium renderer.
/// </summary>
public readonly record struct BrowserFrame(
    byte[] PixelBuffer,
    int Width,
    int Height,
    int Stride,
    float AspectRatio,
    DateTime CapturedAt)
{
    public bool HasBuffer => this.PixelBuffer is { Length: > 0 };
}
