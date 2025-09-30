using System;
using System.Globalization;

namespace Tractus.HtmlToNdi.FramePacing;

public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public static FrameRate Default2997 { get; } = new FrameRate(30000, 1001);

    public double FramesPerSecond => (double)this.Numerator / this.Denominator;

    public static bool TryParse(string value, out FrameRate frameRate)
    {
        value = value.Trim();
        if (value.Contains('/', StringComparison.Ordinal))
        {
            var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) &&
                denominator > 0)
            {
                frameRate = Normalize(numerator, denominator);
                return true;
            }
        }
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            frameRate = FromDouble(fps);
            return true;
        }

        frameRate = default;
        return false;
    }

    public static FrameRate FromDouble(double fps)
    {
        var known = new (double fps, int n, int d)[]
        {
            (23.976, 24000, 1001),
            (29.97, 30000, 1001),
            (59.94, 60000, 1001),
            (119.88, 120000, 1001),
        };

        foreach (var (knownFps, n, d) in known)
        {
            if (Math.Abs(fps - knownFps) < 0.001)
            {
                return new FrameRate(n, d);
            }
        }

        var scaled = (int)Math.Round(fps * 1000.0, MidpointRounding.AwayFromZero);
        var numerator = scaled;
        var denominator = 1000;

        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "Frame rate must be positive.");
        }

        return Normalize(numerator, denominator);
    }

    private static FrameRate Normalize(int numerator, int denominator)
    {
        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Numerator must be positive.");
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be positive.");
        }

        var gcd = GreatestCommonDivisor(Math.Abs(numerator), Math.Abs(denominator));
        return new FrameRate(numerator / gcd, denominator / gcd);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }

        return a;
    }
}
