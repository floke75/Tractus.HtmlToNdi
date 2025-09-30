using System;

using System.Globalization;

namespace Tractus.HtmlToNdi.Video;

public readonly record struct FrameRate(int Numerator, int Denominator)
{
    private static readonly (double Fps, FrameRate Rate)[] KnownRates =
    [
        (23.976023976023978, new FrameRate(24000, 1001)),
        (24d, new FrameRate(24, 1)),
        (25d, new FrameRate(25, 1)),
        (29.97002997002997, new FrameRate(30000, 1001)),
        (30d, new FrameRate(30, 1)),
        (50d, new FrameRate(50, 1)),
        (59.94005994005994, new FrameRate(60000, 1001)),
        (60d, new FrameRate(60, 1)),
        (100d, new FrameRate(100, 1)),
        (120d, new FrameRate(120, 1)),
    ];

    public double ToDouble() => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    public TimeSpan FrameDuration => Numerator == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)Denominator / Numerator);

    public static FrameRate Parse(string? value, FrameRate fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        value = value.Trim();

        if (value.Contains('/'))
        {
            var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) &&
                n > 0 && d > 0)
            {
                return Normalize(new FrameRate(n, d));
            }

            return fallback;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0)
        {
            return FromDouble(fps, fallback);
        }

        return fallback;
    }

    public static FrameRate FromDouble(double fps, FrameRate fallback)
    {
        if (fps <= 0)
        {
            return fallback;
        }

        foreach (var (knownFps, rate) in KnownRates)
        {
            if (Math.Abs(fps - knownFps) <= 0.0005)
            {
                return rate;
            }
        }

        var approx = Approximate(fps, maxDenominator: 100000);
        return approx.Numerator == 0 ? fallback : approx;
    }

    private static FrameRate Normalize(FrameRate rate)
    {
        var gcd = GreatestCommonDivisor(rate.Numerator, rate.Denominator);
        var numerator = rate.Numerator / gcd;
        var denominator = rate.Denominator / gcd;

        if (denominator < 0)
        {
            numerator *= -1;
            denominator *= -1;
        }

        return new FrameRate(numerator, denominator);
    }

    private static FrameRate Approximate(double value, int maxDenominator)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return default;
        }

        var fraction = ContinuedFraction(value, maxDenominator);
        return Normalize(fraction);
    }

    private static FrameRate ContinuedFraction(double value, int maxDenominator)
    {
        long numerator = 1;
        long denominator = 0;
        long prevNumerator = 0;
        long prevDenominator = 1;
        var x = value;

        while (true)
        {
            var integral = (long)Math.Floor(x);
            var nextNumerator = integral * numerator + prevNumerator;
            var nextDenominator = integral * denominator + prevDenominator;

            if (nextDenominator > maxDenominator)
            {
                break;
            }

            prevNumerator = numerator;
            prevDenominator = denominator;
            numerator = nextNumerator;
            denominator = nextDenominator;

            var fractional = x - integral;
            if (fractional < 1e-9)
            {
                break;
            }

            x = 1.0 / fractional;
        }

        if (denominator == 0)
        {
            return default;
        }

        return new FrameRate((int)numerator, (int)denominator);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }

        return Math.Abs(a);
    }
}
