using System;
using System.Globalization;

namespace Tractus.HtmlToNdi.Video;

public readonly struct FrameRate
{
    public int Numerator { get; }
    public int Denominator { get; }

    public double FramesPerSecond => (double)this.Numerator / this.Denominator;
    public TimeSpan FrameInterval => TimeSpan.FromSeconds((double)this.Denominator / this.Numerator);

    private FrameRate(int numerator, int denominator)
    {
        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator));
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        this.Numerator = numerator;
        this.Denominator = denominator;
    }

    public static FrameRate Create(int numerator, int denominator)
    {
        var gcd = GreatestCommonDivisor(Math.Abs(numerator), Math.Abs(denominator));
        return new FrameRate(numerator / gcd, denominator / gcd);
    }

    public static FrameRate FromDouble(double framesPerSecond, int precision = 1000)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        var denominator = Math.Max(1, precision);
        var numerator = (int)Math.Round(framesPerSecond * denominator, MidpointRounding.AwayFromZero);

        if (numerator == 0)
        {
            numerator = 1;
        }

        return Create(numerator, denominator);
    }

    public static bool TryParse(string value, out FrameRate frameRate)
    {
        frameRate = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Contains('/'))
        {
            var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator))
            {
                frameRate = Create(numerator, denominator);
                return true;
            }

            return false;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            frameRate = FromDouble(fps);
            return true;
        }

        return false;
    }

    public override string ToString()
    {
        return $"{this.Numerator}/{this.Denominator} ({this.FramesPerSecond:F3} fps)";
    }

    public static FrameRate Ntsc2997 { get; } = Create(30000, 1001);

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }

        return a == 0 ? 1 : a;
    }
}
