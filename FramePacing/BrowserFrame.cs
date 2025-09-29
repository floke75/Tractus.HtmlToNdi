using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.FramePacing;

/// <summary>
/// Represents a frame produced by the Chromium renderer.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct BrowserFrame(
    nint BufferHandle,
    int Width,
    int Height,
    int Stride,
    float AspectRatio,
    DateTime CapturedAt)
{
    public bool HasBuffer => this.BufferHandle != nint.Zero;
}
