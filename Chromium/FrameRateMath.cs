using System;

namespace Tractus.HtmlToNdi.Chromium;

internal static class FrameRateMath
{
    public static (int Numerator, int Denominator) ToRational(double framesPerSecond)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        const int baseDenominator = 1001;
        var numerator = (int)Math.Round(framesPerSecond * baseDenominator);
        var denominator = baseDenominator;

        if (numerator <= 0)
        {
            numerator = 1;
        }

        var gcd = GreatestCommonDivisor(Math.Abs(numerator), denominator);
        return (numerator / gcd, denominator / gcd);
    }

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
