namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Describes how a captured frame's pixel data is stored.
/// </summary>
internal enum CapturedFrameStorageKind
{
    /// <summary>
    /// Pixels are stored in CPU-accessible memory pointed to by <see cref="CapturedFrame.Buffer"/>.
    /// </summary>
    CpuMemory = 0,

    /// <summary>
    /// Pixels are exposed via a shared GPU texture handle.
    /// </summary>
    SharedTextureHandle = 1,

    /// <summary>
    /// Pixels are exposed via a shared-memory handle which must be mapped before use.
    /// </summary>
    SharedMemoryHandle = 2,
}
