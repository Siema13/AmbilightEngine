// AmbilightEngine.Core/Capture/Processing/ImageProcessor.cs
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
        private float attackFactor = 0.3f;
        private float decayFactor = 0.3f;
        private float sensitivityMultiplier = 1.0f;
        private float minBrightnessFloor = 0f;

        private float brightnessMultiplier = 1.0f;
        private float saturationBoost = 1.0f;
        private float colorTemperatureKelvin = 6500f;
        private int blackCutoffThreshold = 8;
        private float gammaValue = 2.2f;

        private float temperatureFactorR = 1.0f;
        private float temperatureFactorG = 1.0f;
        private float temperatureFactorB = 1.0f;

        // ── Wall Color Compensation ──────────────────────────────────────────────
        // Prekalkulowane mnożniki korekcji kanałów - wyliczane raz przy SetWallColor(),
        // nie w pętli per-pixel (analogicznie do temperatureFactorX).
        private float wallCompR = 1.0f;
        private float wallCompG = 1.0f;
        private float wallCompB = 1.0f;
        private bool wallCompEnabled = false;
        // ────────────────────────────────────────────────────────────────────────

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

        // ── Publiczne API Wall Color Compensation ────────────────────────────────

        /// <summary>
        /// Ustawia korekcję barwy ściany. Wywołaj po każdej zmianie koloru lub siły w UI.
        /// </summary>
        /// <param name="wallR">Kanał R ściany, 0-255</param>
        /// <param name="wallG">Kanał G ściany, 0-255</param>
        /// <param name="wallB">Kanał B ściany, 0-255</param>
        /// <param name="strength">Siła korekcji 0.0 (brak) – 1.0 (pełna)</param>
        public void SetWallColor(byte wallR, byte wallG, byte wallB, float strength)
        {
            strength = Math.Clamp(strength, 0f, 1f);

            if (strength < 0.001f)
            {
                wallCompEnabled = false;
                wallCompR = wallCompG = wallCompB = 1.0f;
                return;
            }

            // Każdy kanał ściany jest traktowany jako "przesunięcie od neutralnego (128)".
            // Ściana mocno czerwona (wallR=220) → kompensujemy wzmacniając niebieski/zielony,
            // osłabiając czerwony; ściana neutralna (128) → mnożnik = 1.0 (brak efektu).
            //
            // Wzór: comp_c = 1.0 + strength * (1.0 - wall_c / 128.0)
            // Przykład: wallR=220, strength=0.5 → compR = 1 + 0.5*(1-220/128) = 1-0.359 = 0.641
            //           wallG=128, strength=0.5 → compG = 1 + 0.5*(1-1.0)    = 1.0
            wallCompR = 1.0f + strength * (1.0f - wallR / 128.0f);
            wallCompG = 1.0f + strength * (1.0f - wallG / 128.0f);
            wallCompB = 1.0f + strength * (1.0f - wallB / 128.0f);

            // Zabezpieczenie przed ujemnymi mnożnikami (bardzo nasycony kolor ściany + pełna siła)
            wallCompR = Math.Max(wallCompR, 0.05f);
            wallCompG = Math.Max(wallCompG, 0.05f);
            wallCompB = Math.Max(wallCompB, 0.05f);

            wallCompEnabled = true;
        }

        /// <summary>
        /// Pomocnicze przeciążenie dla hex-stringa z ustawień (np. "#E8C090").
        /// Wywoływane przez PipelineManager/SettingsImagePage.xaml.cs przy wczytaniu profilu.
        /// </summary>
        public void SetWallColorFromHex(string? hexColor, float strength)
        {
            if (string.IsNullOrWhiteSpace(hexColor))
            {
                SetWallColor(128, 128, 128, 0f);
                return;
            }

            try
            {
                string hex = hexColor.TrimStart('#');
                if (hex.Length == 6)
                {
                    byte r = Convert.ToByte(hex[0..2], 16);
                    byte g = Convert.ToByte(hex[2..4], 16);
                    byte b = Convert.ToByte(hex[4..6], 16);
                    SetWallColor(r, g, b, strength);
                    return;
                }
            }
            catch { /* złe dane – wyłącz kompensację */ }

            SetWallColor(128, 128, 128, 0f);
        }

        // ────────────────────────────────────────────────────────────────────────

        public void ApplyDspParameters(int brightnessPercent, double saturation, int smoothingMs, int blackCutoff, int kelvin, double gamma = 2.2)
        {
            brightnessMultiplier = Math.Clamp(brightnessPercent, 0, 100) / 100f;
            saturationBoost = (float)Math.Clamp(saturation, 0.0, 3.0);
            blackCutoffThreshold = Math.Clamp(blackCutoff, 0, 255);
            colorTemperatureKelvin = Math.Clamp(kelvin, 1000, 12000);
            gammaValue = (float)Math.Clamp(gamma, 1.0, 4.0);

            RecalculateTemperatureFactors();
            float derivedRate = 1.0f / Math.Max(1, smoothingMs / 16f);
            SetDynamics(derivedRate, derivedRate, sensitivityMultiplier, minBrightnessFloor);
        }

        public void SetDynamics(float attack, float decay, float sensitivity, float minBrightness)
        {
            attackFactor = Math.Clamp(attack, 0.01f, 1.0f);
            decayFactor = Math.Clamp(decay, 0.01f, 1.0f);
            sensitivityMultiplier = Math.Clamp(sensitivity, 0.1f, 3.0f);
            minBrightnessFloor = Math.Clamp(minBrightness, 0f, 255f);
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

                float correctedR = rawAvgR * temperatureFactorR;
                float correctedG = rawAvgG * temperatureFactorG;
                float correctedB = rawAvgB * temperatureFactorB;

                float luminance = 0.299f * correctedR + 0.587f * correctedG + 0.114f * correctedB;

                if (luminance < blackCutoffThreshold)
                {
                    correctedR = 0f;
                    correctedG = 0f;
                    correctedB = 0f;
                }
                else if (saturationBoost != 1.0f)
                {
                    correctedR = luminance + (correctedR - luminance) * saturationBoost;
                    correctedG = luminance + (correctedG - luminance) * saturationBoost;
                    correctedB = luminance + (correctedB - luminance) * saturationBoost;

                    correctedR = Math.Clamp(correctedR, 0f, 255f);
                    correctedG = Math.Clamp(correctedG, 0f, 255f);
                    correctedB = Math.Clamp(correctedB, 0f, 255f);
                }

                // ── Wall Color Compensation ───────────────────────────────────────
                // Stosujemy po nasyceniu, przed smoothingiem - korekcja dotyczy wartości
                // "docelowej" koloru, nie skumulowanego stanu EMA. Dzięki temu zmiana
                // siły kompensacji reaguje płynnie przez smoothing, bez artefaktów.
                if (wallCompEnabled)
                {
                    correctedR = Math.Clamp(correctedR * wallCompR, 0f, 255f);
                    correctedG = Math.Clamp(correctedG * wallCompG, 0f, 255f);
                    correctedB = Math.Clamp(correctedB * wallCompB, 0f, 255f);
                }
                // ─────────────────────────────────────────────────────────────────

                float ampR = previousR[i] + (correctedR - previousR[i]) * sensitivityMultiplier;
                float ampG = previousG[i] + (correctedG - previousG[i]) * sensitivityMultiplier;
                float ampB = previousB[i] + (correctedB - previousB[i]) * sensitivityMultiplier;

                float rateR = ampR > previousR[i] ? attackFactor : decayFactor;
                float rateG = ampG > previousG[i] ? attackFactor : decayFactor;
                float rateB = ampB > previousB[i] ? attackFactor : decayFactor;

                float smoothedR = ampR * rateR + previousR[i] * (1.0f - rateR);
                float smoothedG = ampG * rateG + previousG[i] * (1.0f - rateG);
                float smoothedB = ampB * rateB + previousB[i] * (1.0f - rateB);

                previousR[i] = smoothedR;
                previousG[i] = smoothedG;
                previousB[i] = smoothedB;

                float brightR = Math.Clamp(smoothedR * brightnessMultiplier, 0f, 255f);
                float brightG = Math.Clamp(smoothedG * brightnessMultiplier, 0f, 255f);
                float brightB = Math.Clamp(smoothedB * brightnessMultiplier, 0f, 255f);

                float finalR = ApplyGamma(brightR);
                float finalG = ApplyGamma(brightG);
                float finalB = ApplyGamma(brightB);

                if (minBrightnessFloor > 0f)
                {
                    float peak = MathF.Max(finalR, MathF.Max(finalG, finalB));
                    if (peak > 0f && peak < minBrightnessFloor)
                    {
                        float scale = minBrightnessFloor / peak;
                        finalR = Math.Clamp(finalR * scale, 0f, 255f);
                        finalG = Math.Clamp(finalG * scale, 0f, 255f);
                        finalB = Math.Clamp(finalB * scale, 0f, 255f);
                    }
                }

                finalColors[i] = new RgbColor((byte)finalR, (byte)finalG, (byte)finalB);
            }

            return new ReadOnlySpan<RgbColor>(finalColors);
        }

        private float ApplyGamma(float channelValue)
        {
            float normalized = channelValue / 255f;
            float corrected = MathF.Pow(normalized, gammaValue);
            return corrected * 255f;
        }

        public void ClearState()
        {
            Array.Clear(previousR, 0, previousR.Length);
            Array.Clear(previousG, 0, previousG.Length);
            Array.Clear(previousB, 0, previousB.Length);
            Array.Clear(finalColors, 0, finalColors.Length);
        }

        public void SeedState(ImageProcessor previousProcessor)
        {
            if (previousProcessor == null) return;
            if (previousProcessor.previousR.Length != previousR.Length) return;

            Array.Copy(previousProcessor.previousR, previousR, previousR.Length);
            Array.Copy(previousProcessor.previousG, previousG, previousG.Length);
            Array.Copy(previousProcessor.previousB, previousB, previousB.Length);
        }

        public void ApplyColorCalibration(int brightnessPercent, double saturation, int blackCutoff, int kelvin, double gamma)
        {
            brightnessMultiplier = Math.Clamp(brightnessPercent, 0, 100) / 100f;
            saturationBoost = (float)Math.Clamp(saturation, 0.0, 3.0);
            blackCutoffThreshold = Math.Clamp(blackCutoff, 0, 255);
            colorTemperatureKelvin = Math.Clamp(kelvin, 1000, 12000);
            gammaValue = (float)Math.Clamp(gamma, 1.0, 4.0);

            RecalculateTemperatureFactors();
        }

        private void RecalculateTemperatureFactors()
        {
            (float rNeutral, float gNeutral, float bNeutral) = KelvinToRgb(6500f);
            (float rTarget, float gTarget, float bTarget) = KelvinToRgb(colorTemperatureKelvin);

            temperatureFactorR = rNeutral <= 0f ? 1.0f : rTarget / rNeutral;
            temperatureFactorG = gNeutral <= 0f ? 1.0f : gTarget / gNeutral;
            temperatureFactorB = bNeutral <= 0f ? 1.0f : bTarget / bNeutral;
        }

        private static (float r, float g, float b) KelvinToRgb(float kelvin)
        {
            float temp = kelvin / 100f;
            float r, g, b;

            if (temp <= 66f)
            {
                r = 255f;
                g = 99.4708025861f * MathF.Log(temp) - 161.1195681661f;
            }
            else
            {
                float tempMinus60 = temp - 60f;
                r = 329.698727446f * MathF.Pow(tempMinus60, -0.1332047592f);
                g = 288.1221695283f * MathF.Pow(tempMinus60, -0.0755148492f);
            }

            if (temp >= 66f)
            {
                b = 255f;
            }
            else if (temp <= 19f)
            {
                b = 0f;
            }
            else
            {
                b = 138.5177312231f * MathF.Log(temp - 10f) - 305.0447927307f;
            }

            r = Math.Clamp(r, 0f, 255f);
            g = Math.Clamp(g, 0f, 255f);
            b = Math.Clamp(b, 0f, 255f);

            return (r, g, b);
        }
    }
}