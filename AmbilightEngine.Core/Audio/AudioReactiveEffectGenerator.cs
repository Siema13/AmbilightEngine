using System;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Audio
{
    // Zamienia wynik analizy audio (AudioSpectrumFrame) na ramkę kolorów LED (RgbColor[]),
    // zgodnie z wybranym trybem. Analogicznie do PipelineManager.TransitionToStaticColor -
    // ta klasa NIE wysyła nic do urządzenia sama; tylko produkuje docelową ramkę, którą
    // wywołujący (PipelineManager) przekazuje dalej do SendAndRememberFrame/StartFrameTransition.
    public sealed class AudioReactiveEffectGenerator
    {
        private readonly int ledCount;

        // Stan wygaszania błysku dla trybu BeatPulse - niezależny od pojedynczej ramki,
        // bo błysk musi płynnie zgasnąć w ciągu kilku kolejnych klatek, nie tylko tej,
        // w której wykryto beat.
        private float beatPulseIntensity;

        // Współczynnik zgaszania błysku między kolejnymi klatkami (wywoływane ~co ramkę
        // pipeline'u, czyli dużo częściej niż analiza audio) - dobrany tak, by błysk trwał
        // widocznie (~200-300ms), ale nie "wisiał" długo po uderzeniu.
        private const float BeatPulseDecayPerFrame = 0.90f;

        public AudioReactiveEffectGenerator(int ledCount)
        {
            this.ledCount = ledCount;
        }

        public RgbColor[] GenerateFrame(AudioSpectrumFrame audioFrame, AudioReactiveMode mode, RgbColor baseColor)
        {
            return mode switch
            {
                AudioReactiveMode.VuMeter => GenerateVuMeterFrame(audioFrame, baseColor),
                AudioReactiveMode.SpectrumBar => GenerateSpectrumBarFrame(audioFrame),
                AudioReactiveMode.BeatPulse => GenerateBeatPulseFrame(audioFrame, baseColor),
                _ => GenerateVuMeterFrame(audioFrame, baseColor)
            };
        }

        private RgbColor[] GenerateVuMeterFrame(AudioSpectrumFrame audioFrame, RgbColor baseColor)
        {
            var frame = new RgbColor[ledCount];

            // Głośność RMS steruje bezpośrednio jasnością koloru bazowego wybranego przez
            // użytkownika (lub domyślnie białego) - najprostszy, najbardziej "spokojny" efekt.
            float brightness = Math.Clamp(audioFrame.Rms * 1.6f, 0f, 1f);
            var color = ScaleColor(baseColor, brightness);

            Array.Fill(frame, color);
            return frame;
        }

        private RgbColor[] GenerateSpectrumBarFrame(AudioSpectrumFrame audioFrame)
        {
            var frame = new RgbColor[ledCount];

            // FIX (defense-in-depth): Spectrum może teoretycznie być null, jeśli ktoś w przyszłości
            // stworzy AudioSpectrumFrame przez default(...) zamiast AudioSpectrumFrame.Silence(...).
            // Bez tej ochrony wyjątek tutaj cicho zabija pętlę wysyłki ramek w PipelineManager
            // (patrz komentarz przy polu latestAudioFrame) - lepiej zwrócić czarną klatkę.
            int spectrumLength = audioFrame.Spectrum?.Length ?? 0;

            if (spectrumLength == 0)
            {
                Array.Fill(frame, new RgbColor(0, 0, 0));
                return frame;
            }

            for (int led = 0; led < ledCount; led++)
            {
                // Mapujemy pozycję diody na fragment widma (bas na jednym końcu paska,
                // wysokie tony na drugim) - im więcej diod niż binów FFT, tym więcej diod
                // dzieli ten sam fragment widma (i odwrotnie), Array.Length obu stron różni się.
                double spectrumPosition = (double)led / ledCount;

                // Skala logarytmiczna - ucho ludzkie i typowa muzyka mają znacznie więcej
                // istotnej informacji w niskich/średnich częstotliwościach niż w wysokich;
                // mapowanie liniowe "spłaszczałoby" cały ruch do pierwszych kilku diod.
                double logPosition = Math.Pow(spectrumPosition, 2.0);
                int bin = Math.Min(spectrumLength - 1, (int)(logPosition * spectrumLength));

                float magnitude = audioFrame.Spectrum[bin];
                frame[led] = MagnitudeToSpectrumColor(magnitude, spectrumPosition);
            }

            return frame;
        }

        private RgbColor[] GenerateBeatPulseFrame(AudioSpectrumFrame audioFrame, RgbColor baseColor)
        {
            var frame = new RgbColor[ledCount];

            if (audioFrame.BeatDetected)
            {
                beatPulseIntensity = 1f;
            }
            else
            {
                beatPulseIntensity *= BeatPulseDecayPerFrame;
            }

            var color = ScaleColor(baseColor, Math.Clamp(beatPulseIntensity, 0f, 1f));
            Array.Fill(frame, color);
            return frame;
        }

        private static RgbColor ScaleColor(RgbColor color, float factor)
        {
            return new RgbColor(
                (byte)Math.Clamp(color.R * factor, 0f, 255f),
                (byte)Math.Clamp(color.G * factor, 0f, 255f),
                (byte)Math.Clamp(color.B * factor, 0f, 255f));
        }

        // Konwertuje magnitudę pasma + pozycję w widmie na kolor HSV->RGB: odcień (hue) zmienia
        // się wzdłuż paska od czerwieni (bas) przez zieleń do fioletu (wysokie tony) - typowy,
        // czytelny układ kolorów spektrum używany też w LedFx i większości analizatorów audio.
        private static RgbColor MagnitudeToSpectrumColor(float magnitude, double spectrumPosition)
        {
            double hue = spectrumPosition * 270.0; // 0° (czerwony) .. 270° (fiolet)
            double saturation = 1.0;
            double value = Math.Clamp(magnitude, 0f, 1f);

            return HsvToRgb(hue, saturation, value);
        }

        private static RgbColor HsvToRgb(double hue, double saturation, double value)
        {
            double c = value * saturation;
            double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
            double m = value - c;

            double r, g, b;

            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return new RgbColor(
                (byte)Math.Clamp((r + m) * 255.0, 0.0, 255.0),
                (byte)Math.Clamp((g + m) * 255.0, 0.0, 255.0),
                (byte)Math.Clamp((b + m) * 255.0, 0.0, 255.0));
        }
    }
}
