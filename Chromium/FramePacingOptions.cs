using System;

namespace Tractus.HtmlToNdi.Chromium;

public sealed class FramePacingOptions
{
    public FramePacingOptions(double targetFrameRate, int bufferDepth, int windowlessFrameRate)
    {
        if (targetFrameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFrameRate));
        }

        if (bufferDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferDepth));
        }

        if (windowlessFrameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowlessFrameRate));
        }

        this.TargetFrameRate = targetFrameRate;
        this.BufferDepth = bufferDepth;
        this.WindowlessFrameRate = windowlessFrameRate;
        this.TargetInterval = TimeSpan.FromSeconds(1.0 / targetFrameRate);

        (this.FrameRateNumerator, this.FrameRateDenominator) = FrameRateMath.ToRational(targetFrameRate);
    }

    public double TargetFrameRate { get; }

    public TimeSpan TargetInterval { get; }

    public int BufferDepth { get; }

    public int WindowlessFrameRate { get; }

    public int FrameRateNumerator { get; }

    public int FrameRateDenominator { get; }
}
