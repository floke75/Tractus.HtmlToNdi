using System;

namespace Tractus.HtmlToNdi.Video;

public sealed class FramePacerOptions
{
    public bool StartImmediately { get; set; } = true;

    public TimeSpan MetricsLogInterval { get; set; } = TimeSpan.FromSeconds(5);
}