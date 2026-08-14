using System;

namespace AmbilightEngine.Core.ColorCalibration;

/// <summary>
/// Buduje 256-elementowe tablice LUT (look-up table) per kanał RGB, kompensujące
/// nieliniową krzywą PWM chipów WS2812/WS2812B (wewnętrzna rozdzielczość 11-bit
/// mapowana z wejścia 8-bit w sposób nieliniowy - diody świecą słabiej niż liniowo
/// przy niskich kodach wejściowych). Zamiast liczyć Math.Pow() per piksel na żywo,
/// tablica jest przeliczana raz przy zmianie ustawień, a odczyt per piksel jest
/// zwykłym indeksowaniem tablicy - szybciej i dokładniej niż czysta funkcja gamma.
/// </summary>
public static class GammaLutBuilder
{
    private static readonly double[] MeasuredWs2812Curve = BuildMeasuredCurve();

    /// <summary>
    /// Buduje LUT dla jednego kanału koloru.
    /// </summary>
    /// <param name="gain">Wzmocnienie, 1.0 = brak zmiany (typowy zakres 0.2-2.0).</param>
    /// <param name="gamma">Korekcja gamma, 1.0 = brak zmiany (typowy zakres 0.3-3.0).</param>
    /// <param name="offset">Przesunięcie w przestrzeni znormalizowanej -1.0..1.0 (typowy zakres -0.2..0.2).</param>
    public static byte[] BuildChannelLut(double gain, double gamma, double offset)
    {
        if (gain <= 0) throw new ArgumentOutOfRangeException(nameof(gain), "Gain musi być większy od zera.");
        if (gamma <= 0) throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma musi być większa od zera.");

        var lut = new byte[256];

        for (int input = 0; input < 256; input++)
        {
            double normalizedInput = input / 255.0;

            double linear = normalizedInput * gain + offset;
            linear = Clamp01(linear);

            double perceived = Math.Pow(linear, 1.0 / gamma);
            perceived = Clamp01(perceived);

            int compensatedCode = FindClosestCodeForPerceivedBrightness(perceived);
            lut[input] = (byte)compensatedCode;
        }

        return lut;
    }

    private static int FindClosestCodeForPerceivedBrightness(double targetPerceived)
    {
        // Wyszukiwanie binarne, bo MeasuredWs2812Curve jest monotonicznie rosnąca.
        int low = 0, high = 255;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (MeasuredWs2812Curve[mid] < targetPerceived) low = mid + 1;
            else high = mid;
        }

        if (low > 0 && Math.Abs(MeasuredWs2812Curve[low - 1] - targetPerceived) < Math.Abs(MeasuredWs2812Curve[low] - targetPerceived))
        {
            return low - 1;
        }

        return low;
    }

    private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));

    private static double[] BuildMeasuredCurve()
    {
        // Punkty referencyjne oparte na publikowanych pomiarach duty-cycle WS2812
        // (rodzina chipów, ogólny profil - nie pomiar Twojego konkretnego egzemplarza).
        // Format: (kod wejściowy 0-255, znormalizowana jasność wyjściowa 0.0-1.0).
        (int input, double output)[] referencePoints =
        {
            (0, 0.0), (2, 0.0004), (3, 0.001), (5, 0.003), (10, 0.01),
            (16, 0.018), (32, 0.045), (48, 0.075), (64, 0.11), (96, 0.19),
            (128, 0.29), (160, 0.42), (192, 0.58), (224, 0.78), (255, 1.0)
        };

        var curve = new double[256];

        for (int i = 0; i < referencePoints.Length - 1; i++)
        {
            var (x0, y0) = referencePoints[i];
            var (x1, y1) = referencePoints[i + 1];

            for (int x = x0; x <= x1; x++)
            {
                double t = (x1 == x0) ? 0 : (x - x0) / (double)(x1 - x0);
                curve[x] = y0 + t * (y1 - y0);
            }
        }

        return curve;
    }
}