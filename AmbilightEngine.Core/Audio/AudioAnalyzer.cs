using System;
using System.Numerics;
using NAudio.Wave;

namespace AmbilightEngine.Core.Audio
{
    // Przetwarza surowe próbki PCM (float) z AudioCaptureService na gotowe do użycia dane
    // dla generatorów efektów: głośność RMS, energię w trzech pasmach (bas/mid/treble),
    // pełne znormalizowane widmo FFT oraz detekcję uderzeń rytmu (beat) w paśmie basowym.
    //
    // Auto-gain: WLED/reszta systemu nie zna z góry głośności, z jaką użytkownik odtwarza
    // muzykę czy film (zależy od ustawień systemowych, aplikacji, głośności mastera).
    // Żeby efekty świetlne wyglądały podobnie niezależnie od tego, śledzimy ruchomy szczyt
    // energii w każdym paśmie (z wolnym opadaniem - decay) i normalizujemy względem niego,
    // zamiast względem sztywnej stałej.
    public sealed class AudioAnalyzer
    {
        // 2048 próbek przy typowym 48kHz to ~42ms na blok - wystarczająco częste dla płynnych
        // efektów świetlnych (>20 analiz/s), a jednocześnie daje sensowną rozdzielczość
        // częstotliwościową (48000/2048 ≈ 23Hz na "słupek" FFT) do rozróżnienia basu od reszty.
        private const int FftSize = 2048;

        // Liczba używalnych binów FFT (FftSize / 2) - publiczna, żeby PipelineManager mógł
        // zainicjalizować startową (ciszę) ramkę AudioSpectrumFrame.Silence(...) z tablicą
        // Spectrum o właściwej długości, zanim pierwsza realna analiza FFT nadejdzie.
        public const int SpectrumBinCount = FftSize / 2;

        // Współczynnik opadania szczytu auto-gain między kolejnymi blokami - bliski 1.0 oznacza
        // bardzo wolne "zapominanie" poprzednich głośnych fragmentów, żeby ciche pasaże utworu
        // nie powodowały gwałtownego przesterowania efektu do maksimum. Stała niezależna od
        // Sensitivity użytkownika - to osobny mechanizm (auto-gain vs ręczne wzmocnienie).
        private const float PeakDecayFactor = 0.992f;

        // Domyślny próg wzrostu energii basu wymagany do wykrycia beatu - używany, gdy wywołujący
        // nie ustawił jawnie BeatThreshold (patrz właściwość BeatThreshold poniżej). Ta sama
        // wartość co domyślna AudioReactiveSettings.BeatThreshold - zachowuje poprzednie
        // zachowanie dla każdego kodu, który jeszcze nie ustawia tego pola.
        private const float DefaultBeatThreshold = 0.22f;

        // Minimalna wartość szczytu auto-gain - zabezpiecza przed dzieleniem przez ~0 w ciszy
        // (co powodowałoby, że najmniejszy szum tła wygląda jak pełna głośność).
        private const float MinPeakFloor = 0.02f;

        // NAPRAWA "słabe/niestabilne wykrywanie beatu": poprzednia wersja porównywała energię
        // basu tylko z JEDNĄ poprzednią klatką (~42ms wcześniej) - to jest bardzo czułe na
        // zwykły szum FFT między sąsiednimi blokami (fałszywe wykrycia w ciszy/na szumie) i
        // jednocześnie zawodne przy realnym uderzeniu basu rozłożonym na 2-3 klatki (żadna
        // pojedyncza klatka nie musiała przekroczyć progu). Zamiast tego liczymy KRÓTKOTERMINOWĄ
        // ruchomą średnią energii basu z ostatnich ~430ms (BassHistorySize klatek) i wykrywamy
        // beat, gdy aktualna wartość wystrzeliwuje istotnie POWYŻEJ tej średniej - klasyczne
        // podejście "onset detection" używane też w LedFx/podobnych analizatorach audio.
        private const int BassHistorySize = 10;
        private readonly float[] bassHistory = new float[BassHistorySize];
        private int bassHistoryFill;
        private int bassHistoryCount;
        private float bassHistorySum;

        private readonly object stateLock = new object();
        private readonly float[] monoBuffer = new float[FftSize];
        private int monoBufferFill;

        private float bassPeak = MinPeakFloor;
        private float midPeak = MinPeakFloor;
        private float treblePeak = MinPeakFloor;
        private float spectrumPeak = MinPeakFloor;

        private DateTime lastBeatAt = DateTime.MinValue;

        // Wzmocnienie sygnału audio przed analizą RMS/FFT i próg detekcji beatu - ustawiane
        // "na żywo" z UI (AudioReactiveSettings.Sensitivity/BeatThreshold) bez restartu capture
        // WASAPI/analizatora. UWAGA: 'volatile' NIE jest dozwolone na float/double w C# (CS0677),
        // dlatego te pola są chronione tym samym stateLock co reszta stanu analizatora - patrz
        // SetLiveParameters oraz początek OnSamplesAvailable/AnalyzeFilledBuffer.
        private float sensitivity = 1.0f;
        private float beatThreshold = DefaultBeatThreshold;

        // Jedyny bezpieczny wątkowo sposób aktualizacji Sensitivity/BeatThreshold z zewnątrz -
        // wywoływane z PipelineManager.UpdateAudioReactiveParameters (wątek UI) równolegle do
        // OnSamplesAvailable (wątek NAudio), dlatego wymaga tego samego locka.
        public void SetLiveParameters(float newSensitivity, float newBeatThreshold)
        {
            lock (stateLock)
            {
                sensitivity = newSensitivity > 0f ? newSensitivity : 0.01f;
                beatThreshold = Math.Clamp(newBeatThreshold, 0.01f, 1.0f);
            }
        }

        // Minimalny odstęp między wykrytymi beatami - zapobiega "podwójnym" wykryciom na tym
        // samym uderzeniu basu przy typowym tempie muzyki (nawet bardzo szybkie utwory rzadko
        // przekraczają ~300 BPM, czyli beat co ~200ms).
        private static readonly TimeSpan MinBeatInterval = TimeSpan.FromMilliseconds(150);

        public event Action<AudioSpectrumFrame>? FrameAnalyzed;

        // Podłączane bezpośrednio jako handler AudioCaptureService.SamplesAvailable.
        public void OnSamplesAvailable(float[] samples, int sampleCount, WaveFormat format)
        {
            if (format.Channels <= 0) return;

            lock (stateLock)
            {
                int channels = format.Channels;
                int frameCount = sampleCount / channels;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    // Miksujemy wszystkie kanały do mono - efekty świetlne reagują na ogólną
                    // charakterystykę dźwięku, nie na rozkład stereo.
                    float sum = 0f;
                    int baseIndex = frame * channels;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        sum += samples[baseIndex + channel];
                    }

                    monoBuffer[monoBufferFill++] = sum / channels;

                    if (monoBufferFill == FftSize)
                    {
                        AnalyzeFilledBuffer(format.SampleRate);
                        monoBufferFill = 0;
                    }
                }
            }
        }

        private void AnalyzeFilledBuffer(int sampleRate)
        {
            var complexBuffer = new Complex[FftSize];

            // Odczyt Sensitivity POD tym samym stateLock, który już obejmuje całą tę metodę
            // (wywoływaną wyłącznie z OnSamplesAvailable) - zapewnia konsystentny odczyt bez
            // dodatkowego zagnieżdzonego locka.
            float sensitivityGain = sensitivity;

            // Okno Hanna redukuje przeciek widmowy (spectral leakage) wynikający z analizy
            // skończonego, nieokresowego wycinka sygnału - bez niego granice bloku FFT
            // wprowadzałyby sztuczne, szerokopasmowe "szumy" w widmie.
            double rmsAccumulator = 0.0;
            for (int i = 0; i < FftSize; i++)
            {
                // Wzmocnienie (Sensitivity) stosowane PRZED oknem Hanna i FFT - wpływa
                // równomiernie na RMS i wszystkie biny widma, dokładnie jak regulacja "gain"
                // we wzmacniaczu analogowym, zamiast późniejszego przeskalowania wyników.
                float sample = monoBuffer[i] * sensitivityGain;
                rmsAccumulator += sample * sample;

                double window = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
                complexBuffer[i] = new Complex(sample * window, 0.0);
            }

            float rms = (float)Math.Sqrt(rmsAccumulator / FftSize);

            FastFourierTransform.Transform(complexBuffer);

            int usableBins = SpectrumBinCount;
            var magnitudes = new float[usableBins];
            float rawSpectrumPeak = MinPeakFloor;

            for (int bin = 0; bin < usableBins; bin++)
            {
                float magnitude = (float)complexBuffer[bin].Magnitude;
                magnitudes[bin] = magnitude;
                if (magnitude > rawSpectrumPeak) rawSpectrumPeak = magnitude;
            }

            double hzPerBin = (double)sampleRate / FftSize;

            // Granice pasm dobrane empirycznie pod typowe brzmienie muzyki/filmów: bas obejmuje
            // sub-bas i bas (do ~250Hz), mid obejmuje wokale i większość instrumentów (do ~2kHz),
            // treble to wysokie częstotliwości (talerze, syczące spółgłoski) powyżej tego.
            float rawBass = SumMagnitudeRange(magnitudes, hzPerBin, 20, 250);
            float rawMid = SumMagnitudeRange(magnitudes, hzPerBin, 250, 2000);
            float rawTreble = SumMagnitudeRange(magnitudes, hzPerBin, 2000, 16000);

            bassPeak = Math.Max(rawBass, bassPeak * PeakDecayFactor);
            midPeak = Math.Max(rawMid, midPeak * PeakDecayFactor);
            treblePeak = Math.Max(rawTreble, treblePeak * PeakDecayFactor);
            spectrumPeak = Math.Max(rawSpectrumPeak, spectrumPeak * PeakDecayFactor);

            float normalizedBass = Clamp01(rawBass / bassPeak);
            float normalizedMid = Clamp01(rawMid / midPeak);
            float normalizedTreble = Clamp01(rawTreble / treblePeak);

            var normalizedSpectrum = new float[usableBins];
            for (int bin = 0; bin < usableBins; bin++)
            {
                normalizedSpectrum[bin] = Clamp01(magnitudes[bin] / spectrumPeak);
            }

            bool beatDetected = DetectBeat(normalizedBass);
            PushBassHistory(normalizedBass);

            var frame = new AudioSpectrumFrame(
                Clamp01(rms),
                normalizedBass,
                normalizedMid,
                normalizedTreble,
                normalizedSpectrum,
                beatDetected);

            FrameAnalyzed?.Invoke(frame);
        }

        // Dopisuje najnowszą znormalizowaną energię basu do bufora kroczącego (ring buffer),
        // aktualizując sumę w O(1) - odjęcie wartości WYCHODZĄCEJ z okna i dodanie NOWEJ,
        // zamiast przeliczania sumy od zera przy każdej klatce.
        private void PushBassHistory(float normalizedBass)
        {
            bassHistorySum -= bassHistory[bassHistoryFill];
            bassHistory[bassHistoryFill] = normalizedBass;
            bassHistorySum += normalizedBass;

            bassHistoryFill = (bassHistoryFill + 1) % BassHistorySize;
            if (bassHistoryCount < BassHistorySize) bassHistoryCount++;
        }

        private bool DetectBeat(float normalizedBass)
        {
            // Beat = gwałtowny wzrost energii basu względem KRÓTKOTERMINOWEJ ŚREDNIEJ z ostatnich
            // ~430ms (nie tylko poprzedniej klatki) - odróżnia realne uderzenie od zwykłego szumu
            // FFT między sąsiednimi blokami, i jest odporne na to, że narastanie uderzenia basu
            // może być rozłożone na kilka klatek. RiseThreshold jest konfigurowalne (pole
            // beatThreshold, ustawiane przez SetLiveParameters) - MinimumLevel pozostaje stałe,
            // bo to zabezpieczenie przed łapaniem "beatów" w ciszy, niezależne od Sensitivity.
            float riseThreshold = beatThreshold;
            const float MinimumLevel = 0.35f;

            if (normalizedBass < MinimumLevel) return false;

            // Dopóki nie zebrano choć kilku próbek historii, średnia byłaby niereprezentatywna
            // (np. sama pierwsza cisza) - w tym okresie rozgrzewki nie wykrywamy beatów, żeby
            // uniknąć fałszywego wykrycia na starcie odtwarzania.
            if (bassHistoryCount < BassHistorySize) return false;

            float movingAverage = bassHistorySum / BassHistorySize;

            if (normalizedBass - movingAverage < riseThreshold) return false;

            var now = DateTime.UtcNow;
            if (now - lastBeatAt < MinBeatInterval) return false;

            lastBeatAt = now;
            return true;
        }

        private static float SumMagnitudeRange(float[] magnitudes, double hzPerBin, double fromHz, double toHz)
        {
            int fromBin = Math.Max(0, (int)(fromHz / hzPerBin));
            int toBin = Math.Min(magnitudes.Length - 1, (int)(toHz / hzPerBin));

            float sum = 0f;
            for (int bin = fromBin; bin <= toBin; bin++)
            {
                sum += magnitudes[bin];
            }

            return sum;
        }

        private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
    }
}
