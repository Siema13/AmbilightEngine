using System;

namespace AmbilightEngine.Core.Processing
{
    public sealed class BlackBarDetectionService
    {
        private const byte DefaultBlackThreshold = 18;
        private const double DefaultMinBlackRatio = 0.92;

        private const int DefaultRequiredConfirmationFrames = 8;
        private const int DefaultMinimumBarPixels = 12;
        private const int MaximumBarSizeDivisor = 3;
        private const int SymmetryTolerancePixels = 24;

        private BlackBarInsets stableInsets = BlackBarInsets.None;
        private BlackBarInsets candidateInsets = BlackBarInsets.None;
        private int candidateFrameCount;

        public byte BlackThreshold { get; set; } = DefaultBlackThreshold;

        public double MinBlackRatio { get; set; } = DefaultMinBlackRatio;

        public bool IsEnabled { get; set; } = true;

        // Liczba kolejnych analiz, które muszą wskazać ten sam kadr,
        // zanim przebudujemy geometrię stref LED.
        public int RequiredConfirmationFrames { get; set; } = DefaultRequiredConfirmationFrames;

        // Chroni przed wykrywaniem pojedynczych ciemnych linii jako pas filmowy.
        public int MinimumBarPixels { get; set; } = DefaultMinimumBarPixels;

        public BlackBarInsets Detect(byte[] bgraPixels, int width, int height, int stride)
        {
            if (!IsEnabled ||
                bgraPixels is null ||
                width <= 0 ||
                height <= 0 ||
                stride < width * 4)
            {
                Reset();
                return BlackBarInsets.None;
            }

            BlackBarInsets detectedInsets = DetectRawInsets(
                bgraPixels,
                width,
                height,
                stride);

            return ApplyHysteresis(detectedInsets);
        }

        public void Reset()
        {
            stableInsets = BlackBarInsets.None;
            candidateInsets = BlackBarInsets.None;
            candidateFrameCount = 0;
        }

        private BlackBarInsets DetectRawInsets(
            byte[] pixels,
            int width,
            int height,
            int stride)
        {
            int top = ScanHorizontalBar(pixels, width, height, stride, fromTop: true);
            int bottom = ScanHorizontalBar(pixels, width, height, stride, fromTop: false);
            int left = ScanVerticalBar(pixels, width, height, stride, fromLeft: true);
            int right = ScanVerticalBar(pixels, width, height, stride, fromLeft: false);

            bool hasHorizontalBars = IsValidBarPair(
                top,
                bottom,
                height,
                MinimumBarPixels);

            bool hasVerticalBars = IsValidBarPair(
                left,
                right,
                width,
                MinimumBarPixels);

            // Całkowicie czarna lub niemal czarna scena często daje fałszywy wynik:
            // "pasy" jednocześnie na górze, dole, lewej i prawej stronie.
            // Nie zmieniamy wtedy geometrii i zachowujemy ostatni stabilny kadr.
            if (hasHorizontalBars && hasVerticalBars)
            {
                return stableInsets;
            }

            if (hasHorizontalBars)
            {
                return new BlackBarInsets(top, bottom, 0, 0);
            }

            if (hasVerticalBars)
            {
                return new BlackBarInsets(0, 0, left, right);
            }

            return BlackBarInsets.None;
        }

        private bool IsValidBarPair(
            int firstBar,
            int secondBar,
            int dimension,
            int minimumBarPixels)
        {
            if (firstBar < minimumBarPixels || secondBar < minimumBarPixels)
            {
                return false;
            }

            int maximumDifference = Math.Max(
                SymmetryTolerancePixels,
                dimension / 40);

            return Math.Abs(firstBar - secondBar) <= maximumDifference;
        }

        private BlackBarInsets ApplyHysteresis(BlackBarInsets detectedInsets)
        {
            if (detectedInsets == stableInsets)
            {
                candidateInsets = detectedInsets;
                candidateFrameCount = 0;
                return stableInsets;
            }

            if (detectedInsets == candidateInsets)
            {
                candidateFrameCount++;
            }
            else
            {
                candidateInsets = detectedInsets;
                candidateFrameCount = 1;
            }

            int requiredFrames = Math.Max(1, RequiredConfirmationFrames);

            if (candidateFrameCount < requiredFrames)
            {
                return stableInsets;
            }

            stableInsets = candidateInsets;
            candidateFrameCount = 0;

            System.Diagnostics.Debug.WriteLine(
                $"[DIAG] BlackBarDetection: zaakceptowano nowy kadr: " +
                $"Top={stableInsets.Top}, Bottom={stableInsets.Bottom}, " +
                $"Left={stableInsets.Left}, Right={stableInsets.Right}");

            return stableInsets;
        }

        private int ScanHorizontalBar(
            byte[] pixels,
            int width,
            int height,
            int stride,
            bool fromTop)
        {
            int maxScanRows = height / MaximumBarSizeDivisor;
            int detectedRows = 0;

            for (int row = 0; row < maxScanRows; row++)
            {
                int y = fromTop ? row : height - 1 - row;

                if (!IsRowMostlyBlack(pixels, width, stride, y))
                {
                    break;
                }

                detectedRows++;
            }

            return detectedRows;
        }

        private int ScanVerticalBar(
            byte[] pixels,
            int width,
            int height,
            int stride,
            bool fromLeft)
        {
            int maxScanColumns = width / MaximumBarSizeDivisor;
            int detectedColumns = 0;

            for (int column = 0; column < maxScanColumns; column++)
            {
                int x = fromLeft ? column : width - 1 - column;

                if (!IsColumnMostlyBlack(pixels, height, stride, x))
                {
                    break;
                }

                detectedColumns++;
            }

            return detectedColumns;
        }

        private bool IsRowMostlyBlack(
            byte[] pixels,
            int width,
            int stride,
            int y)
        {
            int blackCount = 0;
            int sampleStep = Math.Max(1, width / 96);
            int sampledPixels = 0;

            for (int x = 0; x < width; x += sampleStep)
            {
                int offset = y * stride + x * 4;

                if (offset < 0 || offset + 2 >= pixels.Length)
                {
                    continue;
                }

                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];

                sampledPixels++;

                if (IsBlack(blue, green, red))
                {
                    blackCount++;
                }
            }

            return sampledPixels > 0 &&
                   (double)blackCount / sampledPixels >= MinBlackRatio;
        }

        private bool IsColumnMostlyBlack(
            byte[] pixels,
            int height,
            int stride,
            int x)
        {
            int blackCount = 0;
            int sampleStep = Math.Max(1, height / 96);
            int sampledPixels = 0;

            for (int y = 0; y < height; y += sampleStep)
            {
                int offset = y * stride + x * 4;

                if (offset < 0 || offset + 2 >= pixels.Length)
                {
                    continue;
                }

                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];

                sampledPixels++;

                if (IsBlack(blue, green, red))
                {
                    blackCount++;
                }
            }

            return sampledPixels > 0 &&
                   (double)blackCount / sampledPixels >= MinBlackRatio;
        }

        private bool IsBlack(byte blue, byte green, byte red)
        {
            return blue <= BlackThreshold &&
                   green <= BlackThreshold &&
                   red <= BlackThreshold;
        }
    }

    public readonly struct BlackBarInsets : IEquatable<BlackBarInsets>
    {
        public static readonly BlackBarInsets None = new(0, 0, 0, 0);

        public BlackBarInsets(int top, int bottom, int left, int right)
        {
            Top = Math.Max(0, top);
            Bottom = Math.Max(0, bottom);
            Left = Math.Max(0, left);
            Right = Math.Max(0, right);
        }

        public int Top { get; }

        public int Bottom { get; }

        public int Left { get; }

        public int Right { get; }

        public bool HasAnyBar => Top > 0 || Bottom > 0 || Left > 0 || Right > 0;

        public bool Equals(BlackBarInsets other)
        {
            return Top == other.Top &&
                   Bottom == other.Bottom &&
                   Left == other.Left &&
                   Right == other.Right;
        }

        public override bool Equals(object? obj)
        {
            return obj is BlackBarInsets other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Top, Bottom, Left, Right);
        }

        public static bool operator ==(BlackBarInsets left, BlackBarInsets right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(BlackBarInsets left, BlackBarInsets right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"Top={Top}, Bottom={Bottom}, Left={Left}, Right={Right}";
        }
    }
}