using System;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Launcher;

internal sealed class LaunchConfiguration
{
    public LaunchConfiguration(
        string ndiName,
        int port,
        string url,
        int width,
        int height,
        FrameRate frameRate,
        string frameRateText,
        bool enableBuffering,
        int bufferDepth,
        TimeSpan telemetryInterval,
        int? windowlessFrameRateOverride,
        string? windowlessFrameRateText,
        bool disableGpuVsync,
        bool disableFrameRateLimit)
    {
        NdiName = ndiName;
        Port = port;
        Url = url;
        Width = width;
        Height = height;
        FrameRate = frameRate;
        FrameRateText = frameRateText;
        EnableBuffering = enableBuffering;
        BufferDepth = bufferDepth;
        TelemetryInterval = telemetryInterval;
        WindowlessFrameRateOverride = windowlessFrameRateOverride;
        WindowlessFrameRateText = windowlessFrameRateText;
        DisableGpuVsync = disableGpuVsync;
        DisableFrameRateLimit = disableFrameRateLimit;
    }

    public string NdiName { get; }

    public int Port { get; }

    public string Url { get; }

    public int Width { get; }

    public int Height { get; }

    public FrameRate FrameRate { get; }

    public string FrameRateText { get; }

    public bool EnableBuffering { get; }

    public int BufferDepth { get; }

    public TimeSpan TelemetryInterval { get; }

    public int? WindowlessFrameRateOverride { get; }

    public string? WindowlessFrameRateText { get; }

    public bool DisableGpuVsync { get; }

    public bool DisableFrameRateLimit { get; }

    public LaunchConfiguration With(
        string? ndiName = null,
        int? port = null,
        string? url = null,
        int? width = null,
        int? height = null,
        FrameRate? frameRate = null,
        string? frameRateText = null,
        bool? enableBuffering = null,
        int? bufferDepth = null,
        TimeSpan? telemetryInterval = null,
        int? windowlessFrameRateOverride = null,
        string? windowlessFrameRateText = null,
        bool? disableGpuVsync = null,
        bool? disableFrameRateLimit = null)
    {
        return new LaunchConfiguration(
            ndiName ?? NdiName,
            port ?? Port,
            url ?? Url,
            width ?? Width,
            height ?? Height,
            frameRate ?? FrameRate,
            frameRateText ?? FrameRateText,
            enableBuffering ?? EnableBuffering,
            bufferDepth ?? BufferDepth,
            telemetryInterval ?? TelemetryInterval,
            windowlessFrameRateOverride ?? WindowlessFrameRateOverride,
            windowlessFrameRateText ?? WindowlessFrameRateText,
            disableGpuVsync ?? DisableGpuVsync,
            disableFrameRateLimit ?? DisableFrameRateLimit);
    }
}
