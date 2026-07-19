using System;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.SystemState
{
    public sealed class LoungeLightEffectGenerator
    {
        private readonly int ledCount;
        private readonly RgbColor[] buffer;
        private double phase;
        private double huePhase;

        private const double BreathCycleSeconds = 5.0;
        private const double HueCycleSeconds = 30.0;
        private const double HueRangeDegrees = 40.0;
        private const double FadeDurationSeconds = 1.2;

        private double fadeProgress;
        private RgbColor fadeStartColor;

        public LoungeLightEffectGenerator(int ledCount)
        {
            this.ledCount = ledCount;
            buffer = new RgbColor[ledCount];
        }

        public void BeginFadeIn(RgbColor lastKnownColor)
        {
            fadeStartColor = lastKnownColor;
            fadeProgress = 0.0;
        }

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        public ReadOnlySpan<RgbColor> GenerateNextFrame(double elapsedSecondsSinceLastFrame, byte baseR, byte baseG, byte baseB)
        {
            phase += elapsedSecondsSinceLastFrame / BreathCycleSeconds;
            if (phase > 1.0) phase -= 1.0;

            huePhase += elapsedSecondsSinceLastFrame / HueCycleSeconds;
            if (huePhase > 1.0) huePhase -= 1.0;

            double sinePosition = (Math.Sin(phase * Math.PI * 2) + 1.0) / 2.0;
            double eased = SmoothStep(0.0, 1.0, sinePosition);
            double brightness = 0.08 + eased * 0.92;

            ToHsv(baseR, baseG, baseB, out double h, out double s, out double _);
            double hueShift = Math.Sin(huePhase * Math.PI * 2) * HueRangeDegrees;
            double shiftedHue = (h + hueShift + 360.0) % 360.0;

            FromHsv(shiftedHue, s, brightness, out byte targetR, out byte targetG, out byte targetB);

            byte finalR = targetR;
            byte finalG = targetG;
            byte finalB = targetB;

            if (fadeProgress < 1.0)
            {
                fadeProgress += elapsedSecondsSinceLastFrame / FadeDurationSeconds;
                if (fadeProgress > 1.0) fadeProgress = 1.0;

                double fadeEased = SmoothStep(0.0, 1.0, fadeProgress);
                finalR = (byte)(fadeStartColor.R + (targetR - fadeStartColor.R) * fadeEased);
                finalG = (byte)(fadeStartColor.G + (targetG - fadeStartColor.G) * fadeEased);
                finalB = (byte)(fadeStartColor.B + (targetB - fadeStartColor.B) * fadeEased);
            }

            var color = new RgbColor(finalR, finalG, finalB);
            for (int i = 0; i < ledCount; i++)
            {
                buffer[i] = color;
            }

            return new ReadOnlySpan<RgbColor>(buffer);
        }

        public ReadOnlySpan<RgbColor> GenerateBlackFrame()
        {
            Array.Clear(buffer, 0, buffer.Length);
            return new ReadOnlySpan<RgbColor>(buffer);
        }

        private static void ToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            h = 0;
            if (delta > 0.00001)
            {
                if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
                else if (max == gd) h = 60 * (((bd - rd) / delta) + 2);
                else h = 60 * (((rd - gd) / delta) + 4);
            }
            if (h < 0) h += 360;

            s = max <= 0 ? 0 : delta / max;
            if (s < 0.3) s = 0.7; // Jeśli bazowy kolor jest zbyt "wyblakły", wymuszamy żywszą saturację dla efektu
            v = max;
        }

        private static void FromHsv(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
            double m = v - c;

            double rd, gd, bd;
            if (h < 60) { rd = c; gd = x; bd = 0; }
            else if (h < 120) { rd = x; gd = c; bd = 0; }
            else if (h < 180) { rd = 0; gd = c; bd = x; }
            else if (h < 240) { rd = 0; gd = x; bd = c; }
            else if (h < 300) { rd = x; gd = 0; bd = c; }
            else { rd = c; gd = 0; bd = x; }

            r = (byte)Math.Clamp((rd + m) * 255.0, 0, 255);
            g = (byte)Math.Clamp((gd + m) * 255.0, 0, 255);
            b = (byte)Math.Clamp((bd + m) * 255.0, 0, 255);
        }
    }
}