using System;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Represents a captured video frame from the browser.
/// </summary>
internal readonly struct CapturedFrame : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CapturedFrame"/> struct.
    /// </summary>
    /// <param name="buffer">A pointer to the frame buffer.</param>
    /// <param name="width">The width of the frame.</param>
    /// <param name="height">The height of the frame.</param>
    /// <param name="stride">The stride of the frame.</param>
    /// <param name="monotonicTimestamp">High-resolution timestamp captured alongside the frame, expressed in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="timestampUtc">The UTC wall-clock timestamp for the frame.</param>
    /// <param name="releaseAction">Optional callback invoked when the frame is disposed to release native resources.</param>
    /// <param name="storageKind">Describes how the pixel payload should be accessed by consumers.</param>
    public CapturedFrame(IntPtr buffer, int width, int height, int stride, long monotonicTimestamp, DateTime timestampUtc, Action? releaseAction = null, CapturedFrameStorageKind storageKind = CapturedFrameStorageKind.CpuMemory)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        MonotonicTimestamp = monotonicTimestamp;
        TimestampUtc = timestampUtc;
        ReleaseAction = releaseAction;
        StorageKind = storageKind;
    }

    /// <summary>
    /// Gets a pointer to the frame buffer.
    /// </summary>
    public IntPtr Buffer { get; }

    /// <summary>
    /// Gets the width of the frame.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height of the frame.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the stride of the frame.
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// Gets the high-resolution capture timestamp expressed in <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    public long MonotonicTimestamp { get; }

    /// <summary>
    /// Gets the UTC timestamp for metadata consumers.
    /// </summary>
    public DateTime TimestampUtc { get; }

    /// <summary>
    /// Gets the storage kind that describes how the pixel buffer should be interpreted.
    /// </summary>
    public CapturedFrameStorageKind StorageKind { get; }

    /// <summary>
    /// Gets an optional action that releases the frame resources once the pipeline finishes using it.
    /// </summary>
    public Action? ReleaseAction { get; }

    /// <summary>
    /// Gets the size of the frame in bytes.
    /// </summary>
    public int SizeInBytes => Height * Stride;

    /// <summary>
    /// Releases any native resources associated with the frame.
    /// </summary>
    public void Dispose()
    {
        ReleaseAction?.Invoke();
    }
}
