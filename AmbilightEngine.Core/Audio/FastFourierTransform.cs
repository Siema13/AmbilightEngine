using System;
using System.Numerics;

namespace AmbilightEngine.Core.Audio
{
    // Prosta, samodzielna implementacja FFT (Cooley-Tukey, radix-2, in-place) - rozmiar wejścia
    // MUSI być potęgą dwójki. Celowo nie korzystamy z dodatkowej biblioteki (np. MathNet.Numerics)
    // tylko dla tej jednej funkcji - algorytm jest krótki, dobrze znany i łatwy do zweryfikowania,
    // a dodanie kolejnej zależności NuGet wyłącznie po transformatę byłoby nieproporcjonalne.
    internal static class FastFourierTransform
    {
        // Transformuje bufor w miejscu. `buffer.Length` musi być potęgą dwójki.
        public static void Transform(Complex[] buffer)
        {
            int n = buffer.Length;
            if (n <= 1) return;

            BitReverseShuffle(buffer);

            for (int size = 2; size <= n; size *= 2)
            {
                double angleStep = -2.0 * Math.PI / size;
                var rootOfUnity = new Complex(Math.Cos(angleStep), Math.Sin(angleStep));

                for (int start = 0; start < n; start += size)
                {
                    var current = Complex.One;

                    for (int offset = 0; offset < size / 2; offset++)
                    {
                        int evenIndex = start + offset;
                        int oddIndex = evenIndex + size / 2;

                        Complex even = buffer[evenIndex];
                        Complex odd = buffer[oddIndex] * current;

                        buffer[evenIndex] = even + odd;
                        buffer[oddIndex] = even - odd;

                        current *= rootOfUnity;
                    }
                }
            }
        }

        private static void BitReverseShuffle(Complex[] buffer)
        {
            int n = buffer.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }
                j ^= bit;

                if (i < j)
                {
                    (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
                }
            }
        }

        // Najbliższa potęga dwójki >= value - używane do dopełnienia bufora próbek zerami
        // (zero-padding), jeśli rozmiar bloku audio nie jest naturalnie potęgą dwójki.
        public static int NextPowerOfTwo(int value)
        {
            int power = 1;
            while (power < value) power *= 2;
            return power;
        }
    }
}
