using System;
using System.Diagnostics;
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
    // BassStrobe ~ "Bass strobe", MultiColorSpectrum ~ "Multi color", Bar ~ "Bar",
    // Scroll ~ "Scroll", Fade ~ "Fade", Blocks ~ "Blocks", Wavelength ~ "Wavelength".
    public sealed class AudioReactiveEffectGenerator
    {
        private readonly int ledCount;
        private readonly Random random = new Random();

        // Zegar wewnętrzny dla efektów zależnych od czasu rzeczywistego (Scroll/Wavelength) -
        // mierzony lokalnie, żeby ruch pozostał płynny i niezależny od FPS pipeline'u, bez
        // konieczności przekazywania delta-time przez sygnaturę GenerateFrame (co wymagałoby
        // zmiany wywołującego kodu w PipelineManager).
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double lastElapsedSeconds;

        // Stan wygaszania błysku dla trybów BeatPulse/BassStrobe - niezależny od pojedynczej
        // ramki, bo błysk musi płynnie zgasnąć w ciągu kilku kolejnych klatek, nie tylko tej,
        // w której wykryto beat.
        private float beatPulseIntensity;
        private float bassStrobeIntensity;

        // Pozycja "głowy" animacji dla trybu Wave - w zakresie 0.0..1.0, przesuwana o krok
        // przy każdym wykrytym beacie (nie w czasie rzeczywistym), żeby ruch był wyraźnie
        // zsynchronizowany z rytmem muzyki, a nie tylko z upływem czasu.
        private float wavePosition;

        // Peak indicator dla trybu Bar - narasta natychmiast do aktualnego RMS, ale opada
        // wolno (jak wskaźnik szczytowy analogowego VU-metra), niezależnie od głównego
        // wypełnienia paska, które śledzi RMS bezpośrednio i płynnie.
        private float barPeakLevel;

        // Lista aktywnych "pakietów" koloru w trybie Scroll - każdy ma pozycję (0.0 = start
        // paska, rośnie w czasie) i kolor przypisany w momencie powstania (na beacie).
        // Ograniczona do rozsądnej liczby elementów, żeby uniknąć nieograniczonego wzrostu
        // przy bardzo długim, nieprzerwanym paśmie uderzeń beatu.
        private readonly System.Collections.Generic.List<ScrollPulse> scrollPulses = new();
        private const int MaxScrollPulses = 24;

        // Wygładzona (EMA) głośność dla trybu Fade - aktualizowana co klatkę zgodnie z
        // FadeSmoothing z ustawień, dająca efekt "oddychania" zamiast surowego RMS.
        private float fadeSmoothedRms;

        // Bieżący układ bloków dla trybu Blocks - przelosowywany na każdym beacie, między
        // beatami pozostaje stabilny (żadnego migotania w ciszy/na szumie FFT).
        private RgbColor[]? blocksPattern;
        private int blocksPatternLedCount;
        private int lastBlocksCount;

        public AudioReactiveEffectGenerator(int ledCount)
        {
            this.ledCount = ledCount;
        }

        public RgbColor[] GenerateFrame(
            AudioSpectrumFrame audioFrame,
            AudioReactiveMode mode,
            AudioReactiveSettings settings)
        {
            double elapsedSeconds = clock.Elapsed.TotalSeconds;
            double deltaSeconds = Math.Max(0.0, elapsedSeconds - lastElapsedSeconds);
            lastElapsedSeconds = elapsedSeconds;

            // Ochrona przed nienaturalnie dużym skokiem czasu (np. aplikacja wznowiona po
            // uśpieniu systemu, albo pierwsza klatka po starcie) - bez tego animacje czasowe
            // (Scroll/Wavelength) mogłyby wykonać jeden gigantyczny "skok" w pierwszej klatce.
            deltaSeconds = Math.Min(deltaSeconds, 0.25);

            return mode switch
            {
                AudioReactiveMode.VuMeter => GenerateVuMeterFrame(audioFrame, settings),
                AudioReactiveMode.SpectrumBar => GenerateSpectrumBarFrame(audioFrame),
                AudioReactiveMode.BeatPulse => GenerateBeatPulseFrame(audioFrame, settings),
                AudioReactiveMode.Energy => GenerateEnergyFrame(audioFrame, settings),
                AudioReactiveMode.Wave => GenerateWaveFrame(audioFrame, settings),
                AudioReactiveMode.BassStrobe => GenerateBassStrobeFrame(audioFrame, settings),
                AudioReactiveMode.MultiColorSpectrum => GenerateMultiColorSpectrumFrame(audioFrame, settings),
                AudioReactiveMode.Bar => GenerateBarFrame(audioFrame, settings, deltaSeconds),
                AudioReactiveMode.Scroll => GenerateScrollFrame(audioFrame, settings, deltaSeconds),
                AudioReactiveMode.Fade => GenerateFadeFrame(audioFrame, settings),
                AudioReactiveMode.Blocks => GenerateBlocksFrame(audioFrame, settings),
                AudioReactiveMode.Wavelength => GenerateWavelengthFrame(audioFrame, settings, deltaSeconds),
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

        // Odpowiednik LedFx "Bar" - pasek wypełnia się od pozycji 0 proporcjonalnie do RMS,
        // jak analogowy wskaźnik poziomu. Osobno śledzimy "peak indicator" - jedną diodę tuż
        // za czołem wypełnienia, która narasta natychmiast do nowego szczytu, ale opada wolno
        // (klasyczne zachowanie VU-metra: łatwo zauważyć NAJGŁOŚNIEJSZY moment, nie tylko
        // aktualny poziom).
        private RgbColor[] GenerateBarFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings, double deltaSeconds)
        {
            var frame = new RgbColor[ledCount];

            float level = Math.Clamp(audioFrame.Rms * 1.6f, 0f, 1f);

            // Peak narasta natychmiast do nowego szczytu, opada z prędkością niezależną od
            // FPS (proporcjonalnie do deltaSeconds), żeby zachowanie było identyczne przy
            // różnych częstotliwościach odświeżania pipeline'u.
            const float PeakFallPerSecond = 0.6f;
            if (level > barPeakLevel)
            {
                barPeakLevel = level;
            }
            else
            {
                barPeakLevel = Math.Max(0f, barPeakLevel - (float)(PeakFallPerSecond * deltaSeconds));
            }

            var backgroundColor = ScaleColor(SecondaryColor(settings), 0.12f);
            Array.Fill(frame, backgroundColor);

            int filledCount = (int)(level * ledCount);
            for (int led = 0; led < filledCount && led < ledCount; led++)
            {
                frame[led] = PrimaryColor(settings);
            }

            int peakLed = Math.Clamp((int)(barPeakLevel * ledCount), 0, ledCount - 1);
            frame[peakLed] = ScaleColor(new RgbColor(255, 255, 255), 0.9f);

            return frame;
        }

        // Odpowiednik LedFx "Scroll" - przy każdym wykrytym beacie tworzony jest nowy "pakiet"
        // koloru (kolor zależny od tego, które pasmo - bas/mid/treble - jest w danej chwili
        // dominujące), który następnie przesuwa się wzdłuż paska ze stałą prędkością w czasie
        // rzeczywistym. W przeciwieństwie do Wave (jeden ruszający się blok, pozycja zmienia
        // się tylko na beacie), tu wiele pakietów może być w ruchu jednocześnie, tworząc ciąg
        // przesuwających się impulsów - bliżej rzeczywistego zachowania efektu "Scroll".
        private RgbColor[] GenerateScrollFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings, double deltaSeconds)
        {
            var frame = new RgbColor[ledCount];
            var backgroundColor = ScaleColor(SecondaryColor(settings), 0.10f);
            Array.Fill(frame, backgroundColor);

            if (audioFrame.BeatDetected && scrollPulses.Count < MaxScrollPulses)
            {
                var dominantColor = DominantBandColor(audioFrame, settings);
                scrollPulses.Add(new ScrollPulse(0f, dominantColor));
            }

            // Prędkość przewijania w diodach/sekundę - wystarczająco szybka, żeby impulsy
            // zdążyły przejść cały pasek w ciągu kilku sekund, ale nie tak szybka, że oko
            // nie zdąży ich śledzić.
            const float ScrollSpeedLedsPerSecond = 18f;
            float advance = (float)(ScrollSpeedLedsPerSecond * deltaSeconds);

            for (int i = scrollPulses.Count - 1; i >= 0; i--)
            {
                var pulse = scrollPulses[i];
                float newPosition = pulse.Position + advance;

                if (newPosition >= ledCount)
                {
                    scrollPulses.RemoveAt(i);
                    continue;
                }

                scrollPulses[i] = pulse with { Position = newPosition };
            }

            // Renderujemy w kolejności od najstarszego do najnowszego, żeby nowsze impulsy
            // (bliżej startu paska) nie były przesłonięte przez starsze w razie nakładania.
            foreach (var pulse in scrollPulses)
            {
                int led = Math.Clamp((int)pulse.Position, 0, ledCount - 1);
                frame[led] = pulse.Color;

                // Krótki "ogon" za impulsem - dwie diody z zanikającą jasnością, żeby ruch
                // był bardziej widoczny niż pojedynczy punkt.
                for (int tail = 1; tail <= 2; tail++)
                {
                    int tailLed = led - tail;
                    if (tailLed < 0) break;

                    float tailFade = 1f - (tail / 3f);
                    frame[tailLed] = ScaleColor(pulse.Color, tailFade);
                }
            }

            return frame;
        }

        // Odpowiednik LedFx "Fade" - w przeciwieństwie do Energy (reaguje na surowy RMS
        // klatka-po-klatce), tu głośność jest wygładzana wykładniczo (EMA) zgodnie z
        // FadeSmoothing z ustawień, dając miękkie, powolne "oddychanie" jasnością i barwą
        // gradientu Primary->Secondary, bez ostrych skoków przy pojedynczych uderzeniach.
        private RgbColor[] GenerateFadeFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            var frame = new RgbColor[ledCount];

            float rawRms = Math.Clamp(audioFrame.Rms * 1.6f, 0f, 1f);
            float smoothing = Math.Clamp(settings.FadeSmoothing, 0f, 0.99f);

            fadeSmoothedRms = (fadeSmoothedRms * smoothing) + (rawRms * (1f - smoothing));

            var gradientColor = LerpColor(SecondaryColor(settings), PrimaryColor(settings), fadeSmoothedRms);
            var color = ScaleColor(gradientColor, Math.Max(0.2f, fadeSmoothedRms));

            Array.Fill(frame, color);
            return frame;
        }

        // Odpowiednik LedFx "Blocks" - pasek dzielony na BlocksCount losowych segmentów,
        // każdy w losowo wybranym kolorze (z palety Primary/Secondary/Bass/Mid/Treble).
        // Wzorzec jest przelosowywany WYŁĄCZNIE na wykrytym beacie - między beatami pozostaje
        // całkowicie stabilny, żeby efekt nie migotał losowo na szumie FFT (tylko na realnym
        // rytmie muzyki).
        private RgbColor[] GenerateBlocksFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            int blocksCount = Math.Clamp(settings.BlocksCount, 1, Math.Max(1, ledCount));

            bool needsRebuild = blocksPattern == null
                || blocksPatternLedCount != ledCount
                || lastBlocksCount != blocksCount;

            if (audioFrame.BeatDetected || needsRebuild)
            {
                blocksPattern = BuildBlocksPattern(settings, blocksCount);
                blocksPatternLedCount = ledCount;
                lastBlocksCount = blocksCount;
            }

            // blocksPattern jest zawsze zainicjalizowany powyżej (needsRebuild jest true przy
            // pierwszym wywołaniu, gdy pole jest null) - klon chroni przed edycją współdzielonej
            // tablicy przez wywołującego.
            return (RgbColor[])blocksPattern!.Clone();
        }

        private RgbColor[] BuildBlocksPattern(AudioReactiveSettings settings, int blocksCount)
        {
            var frame = new RgbColor[ledCount];
            var palette = new[]
            {
                PrimaryColor(settings),
                SecondaryColor(settings),
                BassColor(settings),
                MidColor(settings),
                TrebleColor(settings)
            };

            // Granice bloków losowane proporcjonalnie do ledCount, z minimalną szerokością
            // 1 diody, żeby uniknąć zdegenerowanych (zerowej długości) segmentów przy dużej
            // liczbie bloków na krótkim pasku.
            int baseWidth = Math.Max(1, ledCount / blocksCount);
            int led = 0;

            for (int block = 0; block < blocksCount && led < ledCount; block++)
            {
                var blockColor = palette[random.Next(palette.Length)];
                int width = block == blocksCount - 1 ? ledCount - led : baseWidth;

                for (int i = 0; i < width && led < ledCount; i++, led++)
                {
                    frame[led] = blockColor;
                }
            }

            return frame;
        }

        // Odpowiednik LedFx "Wavelength" - pełny gradient tęczowy (HSV, odcień 0-360°)
        // rozciągnięty na całej długości paska, przesuwający się w czasie z prędkością bazową
        // (WavelengthSpeed z ustawień) dodatkowo zwiększaną przez chwilową głośność - im
        // głośniej, tym szybszy, bardziej "energetyczny" przepływ barw wzdłuż paska.
        private RgbColor[] GenerateWavelengthFrame(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings, double deltaSeconds)
        {
            var frame = new RgbColor[ledCount];

            float rms = Math.Clamp(audioFrame.Rms * 1.6f, 0f, 1f);
            float speedCyclesPerSecond = Math.Max(0f, settings.WavelengthSpeed) * (1f + rms * 2f);

            wavelengthPhase += (float)(speedCyclesPerSecond * deltaSeconds);
            wavelengthPhase %= 1f;

            for (int led = 0; led < ledCount; led++)
            {
                double position = (double)led / ledCount;
                double hue = ((position + wavelengthPhase) % 1.0) * 360.0;

                // Jasność lekko modulowana głośnością (nigdy poniżej 0.35, żeby gradient był
                // zawsze widoczny nawet w ciszy) - subtelny dodatek "oddychania" na wierzchu
                // ruchu głównego.
                double value = Math.Clamp(0.35 + rms * 0.65, 0.0, 1.0);
                frame[led] = HsvToRgb(hue, 1.0, value);
            }

            return frame;
        }

        // Faza przepływu gradientu (0.0..1.0) dla trybu Wavelength - osobne pole, bo nazwa
        // wavePosition jest już zajęta przez tryb Wave (inna semantyka: tam to pozycja skokowa
        // na beacie, tu płynny, ciągły przepływ w czasie).
        private float wavelengthPhase;

        // Wybiera kolor odpowiadający aktualnie dominującemu pasmu częstotliwości (bas/mid/
        // treble) - używane przez Scroll, żeby kolor nowego impulsu odzwierciedlał charakter
        // dźwięku, który go wywołał, zamiast być zawsze tym samym stałym kolorem.
        private static RgbColor DominantBandColor(AudioSpectrumFrame audioFrame, AudioReactiveSettings settings)
        {
            if (audioFrame.Bass >= audioFrame.Mid && audioFrame.Bass >= audioFrame.Treble)
            {
                return BassColor(settings);
            }

            return audioFrame.Mid >= audioFrame.Treble ? MidColor(settings) : TrebleColor(settings);
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

        // Pojedynczy "pakiet" koloru w trybie Scroll - rekord (record struct byłby lekko
        // wydajniejszy, ale record class jest wystarczający dla listy o max. MaxScrollPulses
        // elementach) z wbudowanym wsparciem dla wyrażenia "with" używanego przy aktualizacji
        // pozycji bez ręcznego tworzenia nowej instancji pole-po-polu.
        private sealed record ScrollPulse(float Position, RgbColor Color);
    }
}
