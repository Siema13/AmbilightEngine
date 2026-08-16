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

        private readonly float[] previousRawR;
        private readonly float[] previousRawG;
        private readonly float[] previousRawB;
        private bool hasPreviousRaw;

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

        // Niezależne mnożniki kalibracji per kanał RGB (Gain), stosowane po korekcji
        // temperatury barwowej, przed nasyceniem. Koryguje rozjazd koloru ekran <-> LED.
        private float channelGainR = 1.0f;
        private float channelGainG = 1.0f;
        private float channelGainB = 1.0f;
        private byte[] channelLutR = BuildIdentityLut();
        private byte[] channelLutG = BuildIdentityLut();
        private byte[] channelLutB = BuildIdentityLut();

        private static readonly float[] MeasuredWs2812Curve = BuildMeasuredWs2812Curve();
        // NOWOŚĆ: kalibracja per-kanał RGB - model Lift/Gamma/Gain znany z kolorystyki
        // filmowej. Offset (Lift) podnosi/opuszcza czernie danego kanału - koryguje sytuację,
        // gdy dioda "czerwona" świeci lekko pomarańczowo nawet przy zerowym sygnale wejściowym.
        // Gamma koryguje nieliniowość w środkowych tonach NIEZALEŻNIE per kanał (w przeciwieństwie
        // do globalnego gammaValue, które działa jednakowo na R/G/B). Zakres offsetu: -0.2..0.2
        // (w jednostkach znormalizowanych 0..1), zakres gammy per kanał: 0.3..3.0.
        private float channelGammaR = 1.0f;
        private float channelGammaG = 1.0f;
        private float channelGammaB = 1.0f;

        private float channelOffsetR = 0.0f;
        private float channelOffsetG = 0.0f;
        private float channelOffsetB = 0.0f;

        // ── Wall Color Compensation ──────────────────────────────────────────────
        private float wallCompR = 1.0f;
        private float wallCompG = 1.0f;
        private float wallCompB = 1.0f;
        private bool wallCompEnabled = false;
        // ────────────────────────────────────────────────────────────────────────

        private const float AttackSpeedupFactor = 4.0f;

        private float zonePeakWeight = 0.3f;
        private float shadowBoostStrength = 1.0f;
        private byte noiseFloor = 4;

        private int edgeFeatherPixels = 2;
        private const float MinEdgeWeight = 0.05f;
        private const int TargetSamplesPerAxis = 16;

        private float phaseSmoothingStrength = 0.0f;

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

            previousRawR = new float[ledCount];
            previousRawG = new float[ledCount];
            previousRawB = new float[ledCount];
        }

        public void SetWallColor(byte wallR, byte wallG, byte wallB, float strength)
        {
            strength = Math.Clamp(strength, 0f, 1f);

            if (strength < 0.001f)
            {
                wallCompEnabled = false;
                wallCompR = wallCompG = wallCompB = 1.0f;
                return;
            }

            wallCompR = 1.0f + strength * (1.0f - wallR / 128.0f);
            wallCompG = 1.0f + strength * (1.0f - wallG / 128.0f);
            wallCompB = 1.0f + strength * (1.0f - wallB / 128.0f);

            wallCompR = Math.Max(wallCompR, 0.05f);
            wallCompG = Math.Max(wallCompG, 0.05f);
            wallCompB = Math.Max(wallCompB, 0.05f);

            wallCompEnabled = true;
        }

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

        public void ApplyDspParameters(int brightnessPercent, double saturation, int smoothingMs, int blackCutoff, int kelvin, double gamma = 2.2)
        {
            brightnessMultiplier = Math.Clamp(brightnessPercent, 0, 100) / 100f;
            saturationBoost = (float)Math.Clamp(saturation, 0.0, 3.0);
            blackCutoffThreshold = Math.Clamp(blackCutoff, 0, 255);
            colorTemperatureKelvin = Math.Clamp(kelvin, 1000, 12000);
            gammaValue = (float)Math.Clamp(gamma, 1.0, 4.0);

            RecalculateTemperatureFactors();

            float decayMs = smoothingMs;
            float attackMs = smoothingMs / AttackSpeedupFactor;

            float derivedDecayRate = 1.0f / Math.Max(1, decayMs / 16f);
            float derivedAttackRate = 1.0f / Math.Max(1, attackMs / 16f);

            SetDynamics(derivedAttackRate, derivedDecayRate, sensitivityMultiplier, minBrightnessFloor);
        }

        public void SetDynamics(float attack, float decay, float sensitivity, float minBrightness)
        {
            attackFactor = Math.Clamp(attack, 0.01f, 1.0f);
            decayFactor = Math.Clamp(decay, 0.01f, 1.0f);
            sensitivityMultiplier = Math.Clamp(sensitivity, 0.1f, 6.0f);
            minBrightnessFloor = Math.Clamp(minBrightness, 0f, 255f);
        }

        public void SetQuality(int skipStep)
        {
            pixelSkipStep = Math.Clamp(skipStep, 1, 20);
        }

        public void SetAdvancedSampling(
            float peakWeight,
            float shadowBoost,
            byte noiseFloorValue,
            int edgeFeather,
            float phaseSmoothing,
            float channelGainRValue,
            float channelGainGValue,
            float channelGainBValue)
        {
            zonePeakWeight = Math.Clamp(peakWeight, 0f, 1f);
            shadowBoostStrength = Math.Clamp(shadowBoost, 1.0f, 4.0f);
            noiseFloor = noiseFloorValue;
            edgeFeatherPixels = Math.Clamp(edgeFeather, 0, 40);
            phaseSmoothingStrength = Math.Clamp(phaseSmoothing, 0f, 1f);
            channelGainR = Math.Clamp(channelGainRValue, 0.2f, 2.0f);
            channelGainG = Math.Clamp(channelGainGValue, 0.2f, 2.0f);
            channelGainB = Math.Clamp(channelGainBValue, 0.2f, 2.0f);
        }

        // NOWOŚĆ: ustawia pełną kalibrację per-kanał (Gain + Gamma + Offset) dla R/G/B.
        // Wywołuj to OBOK SetAdvancedSampling (który wciąż steruje samym Gain, zachowane
        // dla zgodności z istniejącym UI) - ta metoda dodaje dwa kolejne stopnie swobody
        // per kanał, niezbędne, gdy sam mnożnik nie wystarcza do skorygowania koloru LED.
        public void SetChannelCalibration(
     float gainR, float gammaR, float offsetR,
     float gainG, float gammaG, float offsetG,
     float gainB, float gammaB, float offsetB)
        {
            channelGainR = Math.Clamp(gainR, 0.2f, 2.0f);
            channelGainG = Math.Clamp(gainG, 0.2f, 2.0f);
            channelGainB = Math.Clamp(gainB, 0.2f, 2.0f);

            channelGammaR = Math.Clamp(gammaR, 0.3f, 3.0f);
            channelGammaG = Math.Clamp(gammaG, 0.3f, 3.0f);
            channelGammaB = Math.Clamp(gammaB, 0.3f, 3.0f);

            channelOffsetR = Math.Clamp(offsetR, -0.2f, 0.2f);
            channelOffsetG = Math.Clamp(offsetG, -0.2f, 0.2f);
            channelOffsetB = Math.Clamp(offsetB, -0.2f, 0.2f);

            channelLutR = BuildChannelLut(channelGainR, channelGammaR, channelOffsetR);
            channelLutG = BuildChannelLut(channelGainG, channelGammaG, channelOffsetG);
            channelLutB = BuildChannelLut(channelGainB, channelGammaB, channelOffsetB);
        }

        // NOWOŚĆ: budowa jednej tablicy LUT 256-elementowej. Dla każdego z 256 możliwych
        // wejść liczymy docelową, POSTRZEGANĄ jasność (Lift -> Gamma -> Gain, tak jak
        // dotychczas w ApplyChannelCalibration), a następnie znajdujemy kod wejściowy,
        // który na ZMIERZONEJ krzywej WS2812 faktycznie produkuje tę jasność - to jest
        // różnica względem czystej funkcji gamma, która zakłada liniowy PWM.
        private static byte[] BuildChannelLut(float gain, float gamma, float offset)
        {
            var lut = new byte[256];

            for (int input = 0; input < 256; input++)
            {
                float normalized = input / 255f;
                float lifted = Math.Clamp(normalized + offset, 0f, 1f);
                float gammaCorrected = MathF.Pow(lifted, 1.0f / gamma);
                float targetPerceived = Math.Clamp(gammaCorrected * gain, 0f, 1f);

                lut[input] = FindClosestWs2812Code(targetPerceived);
            }

            return lut;
        }

        private static byte FindClosestWs2812Code(float targetPerceived)
        {
            int low = 0, high = 255;

            while (low < high)
            {
                int mid = (low + high) / 2;
                if (MeasuredWs2812Curve[mid] < targetPerceived) low = mid + 1;
                else high = mid;
            }

            if (low > 0 && MathF.Abs(MeasuredWs2812Curve[low - 1] - targetPerceived) < MathF.Abs(MeasuredWs2812Curve[low] - targetPerceived))
            {
                return (byte)(low - 1);
            }

            return (byte)low;
        }

        private static byte[] BuildIdentityLut()
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++) lut[i] = (byte)i;
            return lut;
        }

        // Punkty referencyjne oparte na publikowanych pomiarach duty-cycle WS2812
        // (profil ogólny rodziny chipów - nie pomiar konkretnego egzemplarza taśmy).
        private static float[] BuildMeasuredWs2812Curve()
        {
            (int input, float output)[] referencePoints =
            {
                (0, 0f), (2, 0.0004f), (3, 0.001f), (5, 0.003f), (10, 0.01f),
                (16, 0.018f), (32, 0.045f), (48, 0.075f), (64, 0.11f), (96, 0.19f),
                (128, 0.29f), (160, 0.42f), (192, 0.58f), (224, 0.78f), (255, 1.0f)
            };

            var curve = new float[256];

            for (int i = 0; i < referencePoints.Length - 1; i++)
            {
                var (x0, y0) = referencePoints[i];
                var (x1, y1) = referencePoints[i + 1];

                for (int x = x0; x <= x1; x++)
                {
                    float t = x1 == x0 ? 0f : (x - x0) / (float)(x1 - x0);
                    curve[x] = y0 + t * (y1 - y0);
                }
            }

            return curve;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ComputeEdgeWeight(int coordinate, int dimensionSize)
        {
            if (edgeFeatherPixels <= 0)
            {
                return 1.0f;
            }

            int distanceFromNearEdge = coordinate;
            int distanceFromFarEdge = (dimensionSize - 1) - coordinate;
            int distanceFromEdge = Math.Min(distanceFromNearEdge, distanceFromFarEdge);

            if (distanceFromEdge >= edgeFeatherPixels)
            {
                return 1.0f;
            }

            float ratio = distanceFromEdge / (float)edgeFeatherPixels;
            return MinEdgeWeight + (1.0f - MinEdgeWeight) * ratio;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<RgbColor> ProcessFrame(ReadOnlySpan<byte> rawPixels, int stride, int imageWidth, int imageHeight)
        {
            for (int i = 0; i < zones.Length; i++)
            {
                CaptureZone zone = zones[i];
                float sumB = 0f, sumG = 0f, sumR = 0f;
                float weightSum = 0f;

                int peakLuminanceScaled = -1;
                byte peakR = 0, peakG = 0, peakB = 0;

                int stepX = Math.Max(1, Math.Min(pixelSkipStep, zone.Width / TargetSamplesPerAxis));
                int stepY = Math.Max(1, Math.Min(pixelSkipStep, zone.Height / TargetSamplesPerAxis));

                for (int y = zone.Y; y < zone.Y + zone.Height; y += stepY)
                {
                    int rowOffset = y * stride;
                    float weightY = ComputeEdgeWeight(y, imageHeight);

                    for (int x = zone.X; x < zone.X + zone.Width; x += stepX)
                    {
                        int offset = rowOffset + x * 4;
                        if (offset + 2 >= rawPixels.Length) continue;

                        byte pixelB = rawPixels[offset];
                        byte pixelG = rawPixels[offset + 1];
                        byte pixelR = rawPixels[offset + 2];

                        float weightX = ComputeEdgeWeight(x, imageWidth);
                        float pixelWeight = weightX * weightY;

                        sumB += pixelB * pixelWeight;
                        sumG += pixelG * pixelWeight;
                        sumR += pixelR * pixelWeight;
                        weightSum += pixelWeight;

                        int pixelLuminanceScaled = 299 * pixelR + 587 * pixelG + 114 * pixelB;
                        if (pixelLuminanceScaled > peakLuminanceScaled)
                        {
                            peakLuminanceScaled = pixelLuminanceScaled;
                            peakR = pixelR;
                            peakG = pixelG;
                            peakB = pixelB;
                        }
                    }
                }

                if (weightSum <= 0f) weightSum = 1f;

                float rawAvgR = sumR / weightSum;
                float rawAvgG = sumG / weightSum;
                float rawAvgB = sumB / weightSum;

                if (phaseSmoothingStrength > 0f && hasPreviousRaw)
                {
                    rawAvgR = previousRawR[i] + (rawAvgR - previousRawR[i]) * (1.0f - phaseSmoothingStrength);
                    rawAvgG = previousRawG[i] + (rawAvgG - previousRawG[i]) * (1.0f - phaseSmoothingStrength);
                    rawAvgB = previousRawB[i] + (rawAvgB - previousRawB[i]) * (1.0f - phaseSmoothingStrength);
                }

                previousRawR[i] = rawAvgR;
                previousRawG[i] = rawAvgG;
                previousRawB[i] = rawAvgB;

                float blendedR = rawAvgR + (peakR - rawAvgR) * zonePeakWeight;
                float blendedG = rawAvgG + (peakG - rawAvgG) * zonePeakWeight;
                float blendedB = rawAvgB + (peakB - rawAvgB) * zonePeakWeight;

                float correctedR = blendedR * temperatureFactorR;
                float correctedG = blendedG * temperatureFactorG;
                float correctedB = blendedB * temperatureFactorB;

                // ── Kalibracja per-kanał RGB: Lift (Offset) → Gamma → Gain ─────────
                // NOWOŚĆ: pełny model Lift/Gamma/Gain per kanał, zamiast samego mnożnika.
                // 1) Offset (Lift) przesuwa czernie kanału - koryguje "podbarwienie" diody
                //    nawet przy zerowym sygnale wejściowym.
                // 2) Gamma per kanał koryguje nieliniowość w środkowych tonach TEGO kanału
                //    niezależnie od pozostałych - w przeciwieństwie do globalnej gammaValue.
                // 3) Gain skaluje wynik na końcu, tak jak dotychczas.
                correctedR = channelLutR[(byte)Math.Clamp(correctedR, 0f, 255f)];
                correctedG = channelLutG[(byte)Math.Clamp(correctedG, 0f, 255f)];
                correctedB = channelLutB[(byte)Math.Clamp(correctedB, 0f, 255f)];
                // ─────────────────────────────────────────────────────────────────

                float denoisedR = ApplyNoiseFloor(correctedR);
                float denoisedG = ApplyNoiseFloor(correctedG);
                float denoisedB = ApplyNoiseFloor(correctedB);

                float shadowedR = ApplyShadowBoost(denoisedR);
                float shadowedG = ApplyShadowBoost(denoisedG);
                float shadowedB = ApplyShadowBoost(denoisedB);

                float gainedR = Math.Clamp(shadowedR * sensitivityMultiplier, 0f, 255f);
                float gainedG = Math.Clamp(shadowedG * sensitivityMultiplier, 0f, 255f);
                float gainedB = Math.Clamp(shadowedB * sensitivityMultiplier, 0f, 255f);

                float luminance = 0.299f * gainedR + 0.587f * gainedG + 0.114f * gainedB;

                if (luminance < blackCutoffThreshold)
                {
                    gainedR = 0f;
                    gainedG = 0f;
                    gainedB = 0f;
                }
                else if (saturationBoost != 1.0f)
                {
                    gainedR = luminance + (gainedR - luminance) * saturationBoost;
                    gainedG = luminance + (gainedG - luminance) * saturationBoost;
                    gainedB = luminance + (gainedB - luminance) * saturationBoost;

                    gainedR = Math.Clamp(gainedR, 0f, 255f);
                    gainedG = Math.Clamp(gainedG, 0f, 255f);
                    gainedB = Math.Clamp(gainedB, 0f, 255f);
                }

                if (wallCompEnabled)
                {
                    gainedR = Math.Clamp(gainedR * wallCompR, 0f, 255f);
                    gainedG = Math.Clamp(gainedG * wallCompG, 0f, 255f);
                    gainedB = Math.Clamp(gainedB * wallCompB, 0f, 255f);
                }

                float rateR = gainedR > previousR[i] ? attackFactor : decayFactor;
                float rateG = gainedG > previousG[i] ? attackFactor : decayFactor;
                float rateB = gainedB > previousB[i] ? attackFactor : decayFactor;

                float smoothedR = gainedR * rateR + previousR[i] * (1.0f - rateR);
                float smoothedG = gainedG * rateG + previousG[i] * (1.0f - rateG);
                float smoothedB = gainedB * rateB + previousB[i] * (1.0f - rateB);

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

            hasPreviousRaw = true;

            return new ReadOnlySpan<RgbColor>(finalColors);
        }

        private float ApplyNoiseFloor(float channelValue)
        {
            if (noiseFloor <= 0 || channelValue <= 0f)
            {
                return Math.Max(0f, channelValue);
            }

            if (channelValue >= noiseFloor)
            {
                return channelValue;
            }

            float ratio = channelValue / noiseFloor;
            return channelValue * ratio;
        }

        private float ApplyShadowBoost(float channelValue)
        {
            if (shadowBoostStrength <= 1.0f)
            {
                return channelValue;
            }

            float normalized = Math.Clamp(channelValue / 255f, 0f, 1f);
            float exponent = 1.0f / shadowBoostStrength;
            float boosted = MathF.Pow(normalized, exponent);
            return boosted * 255f;
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

            Array.Clear(previousRawR, 0, previousRawR.Length);
            Array.Clear(previousRawG, 0, previousRawG.Length);
            Array.Clear(previousRawB, 0, previousRawB.Length);
            hasPreviousRaw = false;
        }

        public void SeedState(ImageProcessor previousProcessor)
        {
            if (previousProcessor == null) return;
            if (previousProcessor.previousR.Length != previousR.Length) return;

            Array.Copy(previousProcessor.previousR, previousR, previousR.Length);
            Array.Copy(previousProcessor.previousG, previousG, previousG.Length);
            Array.Copy(previousProcessor.previousB, previousB, previousB.Length);

            if (previousProcessor.previousRawR.Length == previousRawR.Length)
            {
                Array.Copy(previousProcessor.previousRawR, previousRawR, previousRawR.Length);
                Array.Copy(previousProcessor.previousRawG, previousRawG, previousRawG.Length);
                Array.Copy(previousProcessor.previousRawB, previousRawB, previousRawB.Length);
                hasPreviousRaw = previousProcessor.hasPreviousRaw;
            }
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
        /// <summary>
        /// Aktualizuje tylko temperaturę barwową na żywym procesorze obrazu.
        /// Nie zmienia parametrów DSP, kalibracji ani stanu wygładzania EMA, dzięki czemu
        /// może być wywoływana wielokrotnie podczas płynnej animacji pomiędzy presetami bieli.
        /// </summary>
        public void SetColorTemperatureKelvin(float kelvin)
        {
            colorTemperatureKelvin = Math.Clamp(kelvin, 1000f, 12000f);
            RecalculateTemperatureFactors();
        }

        /// <summary>
        /// Zwraca aktualną, także pośrednią, temperaturę barwową procesora.
        /// Używane jako punkt startowy nowej animacji, gdy użytkownik szybko przełącza presety.
        /// </summary>
        public float GetColorTemperatureKelvin()
        {
            return colorTemperatureKelvin;
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