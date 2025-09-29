using System;

namespace Tractus.HtmlToNdi.Chromium;

internal readonly struct FrameRateRational
{
    public static FrameRateRational Default { get; } = new(60, 1);

    public int Numerator { get; }

    public int Denominator { get; }

    private FrameRateRational(int numerator, int denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    public static FrameRateRational FromFramesPerSecond(double framesPerSecond, int maxDenominator = 1000)
    {
        if (double.IsNaN(framesPerSecond) || double.IsInfinity(framesPerSecond) || framesPerSecond <= 0)
        {
            return Default;
        }

        var fps = Math.Clamp(framesPerSecond, 1.0, 240.0);

        var bestNumerator = 0;
        var bestDenominator = 1;
        var bestError = double.MaxValue;

        for (var denominator = 1; denominator <= maxDenominator; denominator++)
        {
            var numerator = (int)Math.Round(fps * denominator);
            if (numerator <= 0)
            {
                continue;
            }

            var candidate = numerator / (double)denominator;
            var error = Math.Abs(fps - candidate);
            if (error >= bestError)
            {
                continue;
            }

            bestError = error;
            bestNumerator = numerator;
            bestDenominator = denominator;

            if (error < 1e-6)
            {
                break;
            }
        }

        if (bestNumerator == 0)
        {
            return Default;
        }

        var gcd = GreatestCommonDivisor(bestNumerator, bestDenominator);
        return new FrameRateRational(bestNumerator / gcd, bestDenominator / gcd);
    }

    private static int GreatestCommonDivisor(int numerator, int denominator)
    {
        while (denominator != 0)
        {
            var remainder = numerator % denominator;
            numerator = denominator;
            denominator = remainder;
        }

        return Math.Max(numerator, 1);
    }
}