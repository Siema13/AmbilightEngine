using System;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Audio
{
    // Zamienia wynik analizy audio (AudioSpectrumFrame) na ramkę kolorów LED (RgbColor[]),
    // zgodnie z wybranym trybem i pełną konfiguracją użytkownika (AudioReactiveSettings).
    // Analogicznie do PipelineManager.TransitionToStaticColor - ta klasa NIE wysyła nic do
    // urządzenia sama; tylko produkuje docelową ramkę, którą wywołujący (PipelineManager)
    // przekazuje dalej do SendAndRememberFrame/StartFrameTransition.
    //
    // Zestaw trybów wzorowany na najpopularniejszych efektach z LedFx
    // (https://github.com/LedFx/LedFx): VuMeter/Energy ~ "Scan"/"Energy", Wave ~ "Scroll",
    // BassStrobe ~ "Bass strobe", MultiColorSpectrum ~ "Multi color".
    public sealed class AudioReactiveEffectGenerator
    {
        private readonly int ledCount;

        // Stan wygaszania błysku dla trybów BeatPulse/BassStrobe - niezależny od pojedynczej
        // ramki, bo błysk musi płynnie zgasnąć w ciągu kilku kolejnych klatek, nie tylko tej,
        // w której wykryto beat.
        private float beatPulseIntensity;
        private float bassStrobeIntensity;

        // Pozycja "głowy" animacji dla trybu Wave - w zakresie 0.0..1.0, przesuwana o krok
        // przy każdym wykrytym beacie (nie w czasie rzeczywistym), żeby ruch był wyraźnie
        // zsynchronizowany z rytmem muzyki, a nie tylko z upływem czasu.
        private float wavePosition;

        public AudioReactiveEffectGenerator(int ledCount)
        {
            this.ledCount = ledCount;
        }

        public RgbColor[] GenerateFrame(
            AudioSpectrumFrame audioFrame,
            AudioReactiveMode mode,
            AudioReactiveSettings settings)
        {
            return mode switch
            {
                AudioReactiveMode.VuMeter => GenerateVuMeterFrame(audioFrame, settings),
                AudioReactiveMode.SpectrumBar => GenerateSpectrumBarFrame(audioFrame),
                AudioReactiveMode.BeatPulse => GenerateBeatPulseFrame(audioFrame, settings),
                AudioReactiveMode.Energy => GenerateEnergyFrame(audioFrame, settings),
                AudioReactiveMode.Wave => GenerateWaveFrame(audioFrame, settings),
                AudioReactiveMode.BassStrobe => GenerateBassStrobeFrame(audioFrame, settings),
                AudioReactiveMode.MultiColorSpectrum => GenerateMultiColorSpectrumFrame(audioFrame, settings),
                _ => GenerateVuMeterFrame(audioFrame, settings)
            };
        }

        private RgbColor[] GenerateVuMeterFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            // Głośność RMS steruje bezpośrednio jasnością koloru podstawowego wybranego przez
            // użytkownika - najprostszy, najbardziej "spokojny" efekt.
            float brightness = Math.Clamp(audioFrame.Rms * 1.6f, 0f, 1f);
            var color = ScaleColor(PrimaryColor(settings), brightness);

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

        private RgbColor[] GenerateBeatPulseFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            if (audioFrame.BeatDetected)
            {
                beatPulseIntensity = 1f;
            }
            else
            {
                beatPulseIntensity *= EffectiveDecay(settings);
            }

            var color = ScaleColor(PrimaryColor(settings), Math.Clamp(beatPulseIntensity, 0f, 1f));
            Array.Fill(frame, color);
            return frame;
        }

        // Odpowiednik LedFx "Energy" - jasność całego paska śledzi RMS (jak VuMeter), ale kolor
        // jest interpolowany wzdłuż gradientu Primary->Secondary zależnie od aktualnej głośności,
        // zamiast pozostawać jednym stałym kolorem - przy ciszy pasek dryfuje w stronę Secondary,
        // przy szczytach głośności w stronę Primary.
        private RgbColor[] GenerateEnergyFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            float rms = Math.Clamp(audioFrame.Rms * 1.6f, 0f, 1f);
            var gradientColor = LerpColor(SecondaryColor(settings), PrimaryColor(settings), rms);
            var color = ScaleColor(gradientColor, Math.Max(0.15f, rms));

            Array.Fill(frame, color);
            return frame;
        }

        // Odpowiednik LedFx "Scroll"/"Wave" - jednolity blok koloru Primary (na tle Secondary)
        // przesuwający się wzdłuż paska; pozycja przesuwa się skokowo o ustalony krok przy
        // każdym wykrytym beacie, więc ruch jest wyraźnie zsynchronizowany z rytmem utworu.
        private RgbColor[] GenerateWaveFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            if (audioFrame.BeatDetected)
            {
                const float StepPerBeat = 0.12f;
                wavePosition = (wavePosition + StepPerBeat) % 1.0f;
            }

            // Szerokość "bloku" fali proporcjonalna do liczby diod, minimum 2 diody, żeby
            // efekt był widoczny nawet na bardzo krótkich paskach.
            int waveWidth = Math.Max(2, ledCount / 6);
            int headPosition = (int)(wavePosition * ledCount);

            var backgroundColor = ScaleColor(SecondaryColor(settings), 0.25f);
            Array.Fill(frame, backgroundColor);

            for (int offset = 0; offset < waveWidth; offset++)
            {
                int led = (headPosition + offset) % ledCount;

                // Zanikanie jasności od głowy do ogona bloku - wygląda jak "kometa",
                // nie jak sztywno wycięty prostokąt koloru.
                float fade = 1f - ((float)offset / waveWidth);
                frame[led] = ScaleColor(PrimaryColor(settings), fade);
            }

            return frame;
        }

        // Odpowiednik LedFx "Bass strobe" - w przeciwieństwie do BeatPulse (który reaguje na
        // RMS i zawsze jest aktywny), ten tryb pokazuje kolor Secondary jako spokojne tło i
        // tylko na WYKRYTYM uderzeniu basu (BeatDetected == true) zalewa cały pasek jaskrawym
        // błyskiem koloru Primary, który następnie gaśnie zgodnie z Decay.
        private RgbColor[] GenerateBassStrobeFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            if (audioFrame.BeatDetected)
            {
                bassStrobeIntensity = 1f;
            }
            else
            {
                bassStrobeIntensity *= EffectiveDecay(settings);
            }

            float intensity = Math.Clamp(bassStrobeIntensity, 0f, 1f);
            var color = LerpColor(SecondaryColor(settings), PrimaryColor(settings), intensity);

            Array.Fill(frame, color);
            return frame;
        }

        // Odpowiednik LedFx "Multi color" - pasek dzielony na trzy równe segmenty (bas/mid/
        // treble), każdy w osobnym, konfigurowalnym kolorze (BassColor/MidColor/TrebleColor),
        // z jasnością segmentu proporcjonalną do znormalizowanej energii odpowiadającego mu
        // pasma częstotliwości.
        private RgbColor[] GenerateMultiColorSpectrumFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            var bassColor = ScaleColor(BassColor(settings), Math.Clamp(audioFrame.Bass * 1.4f, 0.05f, 1f));
            var midColor = ScaleColor(MidColor(settings), Math.Clamp(audioFrame.Mid * 1.4f, 0.05f, 1f));
            var trebleColor = ScaleColor(TrebleColor(settings), Math.Clamp(audioFrame.Treble * 1.4f, 0.05f, 1f));

            int segmentSize = Math.Max(1, ledCount / 3);

            for (int led = 0; led < ledCount; led++)
            {
                frame[led] = led < segmentSize
                    ? bassColor
                    : led < segmentSize * 2
                        ? midColor
                        : trebleColor;
            }

            return frame;
        }

        // Decay w AudioReactiveSettings jest wyrażony jako wartość 0.0..1.0 ustawiana przez
        // użytkownika w UI (slider) - używana bezpośrednio jako współczynnik zgaszania na
        // klatkę, tak jak wcześniejsza stała BeatPulseDecayPerFrame (domyślna wartość 0.90f
        // w AudioReactiveSettings zachowuje to samo zachowanie sprzed wprowadzenia tego pola).
        private static float EffectiveDecay(AudioReactiveSettings settings) =>
            Math.Clamp(settings.Decay, 0.5f, 0.995f);

        private static RgbColor PrimaryColor(AudioReactiveSettings settings) =>
            new RgbColor(settings.PrimaryColorR, settings.PrimaryColorG, settings.PrimaryColorB);

        private static RgbColor SecondaryColor(AudioReactiveSettings settings) =>
            new RgbColor(settings.SecondaryColorR, settings.SecondaryColorG, settings.SecondaryColorB);

        private static RgbColor BassColor(AudioReactiveSettings settings) =>
            new RgbColor(settings.BassColorR, settings.BassColorG, settings.BassColorB);

        private static RgbColor MidColor(AudioReactiveSettings settings) =>
            new RgbColor(settings.MidColorR, settings.MidColorG, settings.MidColorB);

        private static RgbColor TrebleColor(AudioReactiveSettings settings) =>
            new RgbColor(settings.TrebleColorR, settings.TrebleColorG, settings.TrebleColorB);

        private static RgbColor ScaleColor(RgbColor color, float factor)
        {
            return new RgbColor(
                (byte)Math.Clamp(color.R * factor, 0f, 255f),
                (byte)Math.Clamp(color.G * factor, 0f, 255f),
                (byte)Math.Clamp(color.B * factor, 0f, 255f));
        }

        // Interpolacja liniowa między dwoma kolorami - używana przez Energy/BassStrobe do
        // płynnego przejścia Secondary->Primary zamiast tylko skalowania jasności jednego koloru.
        private static RgbColor LerpColor(RgbColor from, RgbColor to, float progress)
        {
            float clamped = Math.Clamp(progress, 0f, 1f);

            return new RgbColor(
                (byte)Math.Clamp(from.R + (to.R - from.R) * clamped, 0f, 255f),
                (byte)Math.Clamp(from.G + (to.G - from.G) * clamped, 0f, 255f),
                (byte)Math.Clamp(from.B + (to.B - from.B) * clamped, 0f, 255f));
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
