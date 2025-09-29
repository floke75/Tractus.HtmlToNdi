using System.Globalization;

namespace Tractus.HtmlToNdi.Video;

public readonly struct FrameRate
{
    private static readonly (double fps, int numerator, int denominator)[] KnownRates =
    {
        (23.976, 24000, 1001),
        (24.0, 24, 1),
        (25.0, 25, 1),
        (29.97, 30000, 1001),
        (30.0, 30, 1),
        (50.0, 50, 1),
        (59.94, 60000, 1001),
        (60.0, 60, 1),
        (119.88, 120000, 1001),
        (120.0, 120, 1)
    };

    public FrameRate(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator));
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    public double Value => Numerator / (double)Denominator;

    public TimeSpan FrameDuration => TimeSpan.FromSeconds(1.0 / Value);

    public static FrameRate Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new FrameRate(60, 1);
        }

        text = text.Trim();

        if (TryParseFraction(text, out var fraction))
        {
            return fraction;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return FromDouble(value);
        }

        throw new FormatException($"Unable to parse frame rate from '{text}'.");
    }

    public static FrameRate FromDouble(double fps)
    {
        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps));
        }

        foreach (var (knownFps, numerator, denominator) in KnownRates)
        {
            if (Math.Abs(knownFps - fps) < 0.0005)
            {
                return new FrameRate(numerator, denominator);
            }
        }

        if (Math.Abs(fps - Math.Round(fps)) < 0.000001)
        {
            return new FrameRate((int)Math.Round(fps), 1);
        }

        // Convert to a reasonable fraction using continued fractions up to denominator 1000.
        const int maxDenominator = 1000;
        var fraction = ToFraction(fps, maxDenominator);
        return new FrameRate(fraction.numerator, fraction.denominator);
    }

    private static bool TryParseFraction(string text, out FrameRate rate)
    {
        rate = default;
        var separators = new[] { '/', ':' };
        foreach (var separator in separators)
        {
            var parts = text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) &&
                numerator > 0 && denominator > 0)
            {
                rate = new FrameRate(numerator, denominator);
                return true;
            }
        }

        return false;
    }

    private static (int numerator, int denominator) ToFraction(double value, int maxDenominator)
    {
        var fraction = ContinuedFraction(value, maxDenominator);
        return (fraction.numerator, fraction.denominator);
    }

    private static (int numerator, int denominator) ContinuedFraction(double value, int maxDenominator)
    {
        int previousNumerator = 0;
        int numerator = 1;
        int previousDenominator = 1;
        int denominator = 0;

        var fraction = value;

        while (true)
        {
            var integralPart = (int)Math.Floor(fraction);
            var tempNumerator = integralPart * numerator + previousNumerator;
            var tempDenominator = integralPart * denominator + previousDenominator;

            if (tempDenominator > maxDenominator)
            {
                break;
            }

            previousNumerator = numerator;
            numerator = tempNumerator;
            previousDenominator = denominator;
            denominator = tempDenominator;

            var delta = fraction - integralPart;
            if (Math.Abs(delta) < 1e-9)
            {
                break;
            }

            fraction = 1.0 / delta;
        }

        if (denominator == 0)
        {
            return ((int)Math.Round(value), 1);
        }

        return (numerator, denominator);
    }

    public override string ToString() => $"{Numerator}/{Denominator}";
}
