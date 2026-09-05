namespace AmbilightEngine.Core.Audio
{
    // Wynik jednej analizy bloku próbek audio - gotowe do zużycia przez generator efektów,
    // bez konieczności znajomości szczegółów FFT/DSP po stronie odbiorcy.
    public readonly struct AudioSpectrumFrame
    {
        // Głośność ogólna (RMS), znormalizowana do zakresu 0.0 .. 1.0 (z lekkim naddatkiem
        // ponad 1.0 możliwym przy bardzo głośnym materiale - odbiorca powinien clampować).
        public readonly float Rms;

        // Energia w trzech szerokich pasmach częstotliwości, znormalizowana względem
        // ruchomej maksymalnej energii widzianej ostatnio (auto-gain) - dzięki temu efekty
        // reagują podobnie niezależnie od głośności ustawionej w systemie/aplikacji źródłowej.
        public readonly float Bass;
        public readonly float Mid;
        public readonly float Treble;

        // Pełne znormalizowane widmo (magnitudy FFT po auto-gain), używane przez tryb
        // "spektrum kolorowe" do mapowania 1:1 pasmo->dioda. Długość = FftSize / 2.
        public readonly float[] Spectrum;

        // True, jeśli w tym blokupróbek wykryto uderzenie rytmu (beat) w paśmie basowym -
        // używane przez tryb "puls na beat".
        public readonly bool BeatDetected;

        public AudioSpectrumFrame(float rms, float bass, float mid, float treble, float[] spectrum, bool beatDetected)
        {
            Rms = rms;
            Bass = bass;
            Mid = mid;
            Treble = treble;
            Spectrum = spectrum;
            BeatDetected = beatDetected;
        }

        public static AudioSpectrumFrame Silence(int spectrumLength) =>
            new AudioSpectrumFrame(0f, 0f, 0f, 0f, new float[spectrumLength], false);
    }
}
