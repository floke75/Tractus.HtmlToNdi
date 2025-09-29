using System;
using System.Globalization;

namespace Tractus.HtmlToNdi.Video;

public readonly struct FrameRate
{
    private static readonly (double fps, int n, int d)[] KnownBroadcastRates =
    {
        (23.976, 24000, 1001),
        (24.0, 24, 1),
        (25.0, 25, 1),
        (29.97, 30000, 1001),
        (30.0, 30, 1),
        (50.0, 50, 1),
        (59.94, 60000, 1001),
        (60.0, 60, 1),
        (120.0, 120, 1)
    };

    public FrameRate(double hertz, int numerator, int denominator)
    {
        if (hertz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hertz));
        }

        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator));
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        Hertz = hertz;
        Numerator = numerator;
        Denominator = denominator;
    }

    public double Hertz { get; }

    public int Numerator { get; }

    public int Denominator { get; }

    public TimeSpan FrameDuration => TimeSpan.FromSeconds(1d / Hertz);

    public static FrameRate Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FromDouble(60.0);
        }

        value = value.Trim();

        if (TryParseRational(value, out var numerator, out var denominator))
        {
            return new FrameRate((double)numerator / denominator, numerator, denominator);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0)
        {
            return FromDouble(fps);
        }

        throw new FormatException($"Unable to parse frame rate '{value}'.");
    }

    public static FrameRate FromDouble(double fps)
    {
        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps));
        }

        foreach (var (knownFps, knownN, knownD) in KnownBroadcastRates)
        {
            if (Math.Abs(knownFps - fps) < 0.005)
            {
                return new FrameRate(knownFps, knownN, knownD);
            }
        }

        if (Math.Abs(fps % 1) < 0.0001)
        {
            return new FrameRate(fps, (int)Math.Round(fps), 1);
        }

        var (n, d) = ApproximateRational(fps, 1001);
        return new FrameRate((double)n / d, n, d);
    }

    private static bool TryParseRational(string value, out int numerator, out int denominator)
    {
        numerator = 0;
        denominator = 0;

        var separatorIndex = value.IndexOfAny(new[] { '/', ':' });
        if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
        {
            return false;
        }

        var numeratorText = value[..separatorIndex];
        var denominatorText = value[(separatorIndex + 1)..];

        if (!int.TryParse(numeratorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator))
        {
            return false;
        }

        if (!int.TryParse(denominatorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator))
        {
            return false;
        }

        return numerator > 0 && denominator > 0;
    }

    private static (int numerator, int denominator) ApproximateRational(double value, int maxDenominator)
    {
        var fraction = ContinuedFraction(value, maxDenominator);
        return (fraction.numerator, fraction.denominator);
    }

    private static (int numerator, int denominator) ContinuedFraction(double value, int maxDenominator)
    {
        var h1 = 1;
        var h2 = 0;
        var k1 = 0;
        var k2 = 1;
        var b = value;

        while (true)
        {
            var a = (int)Math.Floor(b);
            var h = checked(a * h1 + h2);
            var k = checked(a * k1 + k2);

            if (k > maxDenominator)
            {
                return (h1, k1);
            }

            if (Math.Abs(value - (double)h / k) < 1e-9)
            {
                return (h, k);
            }

            h2 = h1;
            h1 = h;
            k2 = k1;
            k1 = k;

            var fractional = b - a;
            if (fractional == 0)
            {
                return (h, k);
            }

            b = 1.0 / fractional;
        }
    }
}
