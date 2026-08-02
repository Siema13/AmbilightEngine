using System;

namespace AmbilightEngine.Core.Processing
{
    public sealed class BlackBarDetectionService
    {
        private const byte DefaultBlackThreshold = 18;
        private const double DefaultMinBlackRatio = 0.92;

        private int smoothedTopBarPx;
        private int smoothedBottomBarPx;
        private int smoothedLeftBarPx;
        private int smoothedRightBarPx;

        public byte BlackThreshold { get; set; } = DefaultBlackThreshold;
        public double MinBlackRatio { get; set; } = DefaultMinBlackRatio;
        public bool IsEnabled { get; set; } = true;

        public BlackBarInsets Detect(byte[] bgraPixels, int width, int height, int stride)
        {
            if (!IsEnabled || bgraPixels == null || width <= 0 || height <= 0)
            {
                return BlackBarInsets.None;
            }

            int topBar = ScanHorizontalBar(bgraPixels, width, height, stride, fromTop: true);
            int bottomBar = ScanHorizontalBar(bgraPixels, width, height, stride, fromTop: false);
            int leftBar = ScanVerticalBar(bgraPixels, width, height, stride, fromLeft: true);
            int rightBar = ScanVerticalBar(bgraPixels, width, height, stride, fromLeft: false);

            smoothedTopBarPx = SmoothValue(smoothedTopBarPx, topBar);
            smoothedBottomBarPx = SmoothValue(smoothedBottomBarPx, bottomBar);
            smoothedLeftBarPx = SmoothValue(smoothedLeftBarPx, leftBar);
            smoothedRightBarPx = SmoothValue(smoothedRightBarPx, rightBar);

            return new BlackBarInsets(
                smoothedTopBarPx,
                smoothedBottomBarPx,
                smoothedLeftBarPx,
                smoothedRightBarPx);
        }

        private static int SmoothValue(int current, int target)
        {
            if (target == current)
            {
                return current;
            }

            // Wygładzanie, żeby pasy nie "skakały" przy szumie w ciemnych scenach.
            return current + (int)Math.Round((target - current) * 0.35);
        }

        private int ScanHorizontalBar(byte[] pixels, int width, int height, int stride, bool fromTop)
        {
            int maxScanRows = height / 3;
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

        private int ScanVerticalBar(byte[] pixels, int width, int height, int stride, bool fromLeft)
        {
            int maxScanColumns = width / 3;
            int detectedColumns = 0;

            for (int col = 0; col < maxScanColumns; col++)
            {
                int x = fromLeft ? col : width - 1 - col;

                if (!IsColumnMostlyBlack(pixels, height, stride, x))
                {
                    break;
                }

                detectedColumns++;
            }

            return detectedColumns;
        }

        private bool IsRowMostlyBlack(byte[] pixels, int width, int stride, int y)
        {
            int blackCount = 0;
            int sampleStep = Math.Max(1, width / 64);
            int sampledPixels = 0;

            for (int x = 0; x < width; x += sampleStep)
            {
                int offset = y * stride + x * 4;

                if (offset + 2 >= pixels.Length)
                {
                    continue;
                }

                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];

                sampledPixels++;

                if (b <= BlackThreshold && g <= BlackThreshold && r <= BlackThreshold)
                {
                    blackCount++;
                }
            }

            if (sampledPixels == 0)
            {
                return false;
            }

            return (double)blackCount / sampledPixels >= MinBlackRatio;
        }

        private bool IsColumnMostlyBlack(byte[] pixels, int height, int stride, int x)
        {
            int blackCount = 0;
            int sampleStep = Math.Max(1, height / 64);
            int sampledPixels = 0;

            for (int y = 0; y < height; y += sampleStep)
            {
                int offset = y * stride + x * 4;

                if (offset + 2 >= pixels.Length)
                {
                    continue;
                }

                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];

                sampledPixels++;

                if (b <= BlackThreshold && g <= BlackThreshold && r <= BlackThreshold)
                {
                    blackCount++;
                }
            }

            if (sampledPixels == 0)
            {
                return false;
            }

            return (double)blackCount / sampledPixels >= MinBlackRatio;
        }
    }

    public readonly struct BlackBarInsets
    {
        public static readonly BlackBarInsets None = new BlackBarInsets(0, 0, 0, 0);

        public BlackBarInsets(int top, int bottom, int left, int right)
        {
            Top = top;
            Bottom = bottom;
            Left = left;
            Right = right;
        }

        public int Top { get; }
        public int Bottom { get; }
        public int Left { get; }
        public int Right { get; }

        public bool HasAnyBar => Top > 0 || Bottom > 0 || Left > 0 || Right > 0;
    }
}