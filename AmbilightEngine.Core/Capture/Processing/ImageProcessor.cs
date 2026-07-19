using System;
using System.Runtime.CompilerServices;

namespace AmbilightEngine.Core.Processing
{
    public sealed class ImageProcessor
    {
        private readonly CaptureZone[] zones;
        private readonly RgbColor[] finalColors;

        private readonly float[] previousR;
        private readonly float[] previousG;
        private readonly float[] previousB;

        private int pixelSkipStep = 4;
        private float smoothingFactor = 0.3f;

        public ImageProcessor(CaptureZone[] zones)
        {
            if (zones == null || zones.Length == 0)
                throw new ArgumentException("Musisz zdefiniować przynajmniej jedną strefę diod.");

            this.zones = zones;
            int ledCount = zones.Length;

            finalColors = new RgbColor[ledCount];
            previousR = new float[ledCount];
            previousG = new float[ledCount];
            previousB = new float[ledCount];
        }

        public void SetSmoothing(float factor)
        {
            smoothingFactor = Math.Clamp(factor, 0.01f, 1.0f);
        }

        public void SetQuality(int skipStep)
        {
            pixelSkipStep = Math.Clamp(skipStep, 1, 20);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<RgbColor> ProcessFrame(ReadOnlySpan<byte> rawPixels, int stride)
        {
            for (int i = 0; i < zones.Length; i++)
            {
                CaptureZone zone = zones[i];
                long sumB = 0, sumG = 0, sumR = 0;
                int pixelCount = 0;

                for (int y = zone.Y; y < zone.Y + zone.Height; y += pixelSkipStep)
                {
                    int rowOffset = y * stride;

                    for (int x = zone.X; x < zone.X + zone.Width; x += pixelSkipStep)
                    {
                        int offset = rowOffset + x * 4;
                        if (offset + 2 >= rawPixels.Length) continue;

                        sumB += rawPixels[offset];
                        sumG += rawPixels[offset + 1];
                        sumR += rawPixels[offset + 2];
                        pixelCount++;
                    }
                }

                if (pixelCount == 0) pixelCount = 1;

                float rawAvgR = sumR / (float)pixelCount;
                float rawAvgG = sumG / (float)pixelCount;
                float rawAvgB = sumB / (float)pixelCount;

                float newR = rawAvgR * smoothingFactor + previousR[i] * (1.0f - smoothingFactor);
                float newG = rawAvgG * smoothingFactor + previousG[i] * (1.0f - smoothingFactor);
                float newB = rawAvgB * smoothingFactor + previousB[i] * (1.0f - smoothingFactor);

                previousR[i] = newR;
                previousG[i] = newG;
                previousB[i] = newB;

                finalColors[i] = new RgbColor((byte)newR, (byte)newG, (byte)newB);
            }

            return new ReadOnlySpan<RgbColor>(finalColors);
        }

        public void ClearState()
        {
            Array.Clear(previousR, 0, previousR.Length);
            Array.Clear(previousG, 0, previousG.Length);
            Array.Clear(previousB, 0, previousB.Length);
            Array.Clear(finalColors, 0, finalColors.Length);
        }
    }
}