using System.Globalization;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Represents a rational frame rate used for Chromium and NDI pacing.
/// </summary>
public readonly struct FrameRate : IEquatable<FrameRate>
{
    private static readonly IReadOnlyDictionary<double, (int N, int D)> KnownFractional =
        new Dictionary<double, (int, int)>
        {
            { 23.976, (24000, 1001) },
            { 29.97, (30000, 1001) },
            { 47.952, (48000, 1001) },
            { 59.94, (60000, 1001) },
            { 71.928, (72000, 1001) },
            { 119.88, (120000, 1001) },
        };

    public FrameRate(int numerator, int denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("Frame-rate denominator cannot be zero.");
        }

        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Frame-rate numerator must be positive.");
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    public double ToDouble() => (double)Numerator / Denominator;

    public TimeSpan FrameInterval => TimeSpan.FromSeconds(Denominator / (double)Numerator);

    public static FrameRate FromDouble(double fps)
    {
        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps));
        }

        foreach (var kvp in KnownFractional)
        {
            if (Math.Abs(kvp.Key - fps) < 0.0005)
            {
                return new FrameRate(kvp.Value.N, kvp.Value.D);
            }
        }

        // Try to approximate to a rational with denominator up to 1000.
        var denominator = 1;
        while (denominator < 1000)
        {
            var numerator = (int)Math.Round(fps * denominator);
            if (Math.Abs(fps - (double)numerator / denominator) < 1e-6)
            {
                return new FrameRate(numerator, denominator);
            }

            denominator++;
        }

        // Fall back to integer numerator / 1.
        return new FrameRate((int)Math.Round(fps), 1);
    }

    public static bool TryParse(string? value, out FrameRate frameRate)
    {
        frameRate = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Contains('/', StringComparison.Ordinal))
        {
            var parts = value.Split('/', 2);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) &&
                denominator != 0)
            {
                frameRate = new FrameRate(numerator, denominator);
                return true;
            }

            return false;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            if (fps > 0)
            {
                frameRate = FromDouble(fps);
                return true;
            }
        }

        return false;
    }

    public override string ToString() => $"{Numerator}/{Denominator}";

    public override bool Equals(object? obj) => obj is FrameRate other && Equals(other);

    public bool Equals(FrameRate other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public static bool operator ==(FrameRate left, FrameRate right) => left.Equals(right);

    public static bool operator !=(FrameRate left, FrameRate right) => !(left == right);
}
