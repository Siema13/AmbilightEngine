namespace AmbilightEngine.Core.Audio
{
    // Kompletny zestaw parametrów konfiguracyjnych trybu Audio Reactive - niezależny od
    // AudioReactiveMode (który tylko wybiera GENERATOR efektu), bo większość tych ustawień
    // (czułość, prędkość zgaszania, próg beatu) wpływa na wszystkie generatory jednocześnie,
    // a kolory są odczytywane selektywnie w zależności od aktywnego trybu.
    //
    // Klasa jest zwykłym POCO (bez logiki) - żeby System.Text.Json mógł ją zserializować
    // jako zagnieżdżoną właściwość w AmbilightSettings/SceneProfile bez dodatkowych atrybutów,
    // tak jak już robi to AmbientEffectConfig w tym samym projekcie.
    public sealed class AudioReactiveSettings
    {
        // Wzmocnienie sygnału audio przed analizą FFT/RMS (0.25 = bardzo ciche źródła,
        // 3.0 = bardzo głośne wzmocnienie) - kompensuje różnice głośności między systemami/
        // aplikacjami źródłowymi, zamiast wymagać od użytkownika zmiany głośności w Windows.
        public float Sensitivity { get; set; } = 1.0f;

        // Współczynnik zgaszania między kolejnymi klatkami dla efektów pulsujących
        // (BeatPulse/BassStrobe/Wave) - bliżej 1.0 = dłuższe, płynniejsze wygaszanie,
        // bliżej 0.0 = szybkie, ostre zgaszenie. Odpowiednik BeatPulseDecayPerFrame,
        // wcześniej sztywnej stałej w AudioReactiveEffectGenerator.
        public float Decay { get; set; } = 0.90f;

        // Próg wzrostu znormalizowanej energii basu wymagany do wykrycia uderzenia (beatu) -
        // niżej = czulsza detekcja (więcej wykryć, ryzyko "false positive" na szumie),
        // wyżej = tylko bardzo wyraźne uderzenia. Odpowiednik RiseThreshold, wcześniej
        // sztywnej stałej w AudioAnalyzer.DetectBeat.
        public float BeatThreshold { get; set; } = 0.22f;

        // Kolor podstawowy - używany jako jedyny kolor przez VuMeter/BeatPulse/BassStrobe,
        // i jako początek gradientu przez Energy/Wave.
        public byte PrimaryColorR { get; set; } = 255;
        public byte PrimaryColorG { get; set; } = 255;
        public byte PrimaryColorB { get; set; } = 255;

        // Kolor dodatkowy - koniec gradientu w Energy/Wave, kolor tła (poza błyskiem)
        // w BassStrobe. Domyślnie czarny (brak podświetlenia tła), zachowując dotychczasowe
        // zachowanie BeatPulse/BassStrobe sprzed wprowadzenia tego pola.
        public byte SecondaryColorR { get; set; } = 0;
        public byte SecondaryColorG { get; set; } = 0;
        public byte SecondaryColorB { get; set; } = 0;

        // Kolory per-pasmo dla MultiColorSpectrum - domyślnie czerwony/zielony/niebieski,
        // czytelny punkt startowy bez konieczności ręcznej konfiguracji przy pierwszym użyciu.
        public byte BassColorR { get; set; } = 255;
        public byte BassColorG { get; set; } = 0;
        public byte BassColorB { get; set; } = 0;

        public byte MidColorR { get; set; } = 0;
        public byte MidColorG { get; set; } = 255;
        public byte MidColorB { get; set; } = 0;

        public byte TrebleColorR { get; set; } = 0;
        public byte TrebleColorG { get; set; } = 128;
        public byte TrebleColorB { get; set; } = 255;

        // Bazowa prędkość przepływu gradientu w trybie Wavelength (obroty pełnego cyklu
        // barw na sekundę przy całkowitej ciszy) - RMS dodaje do tego dodatkowy, chwilowy
        // przyrost, więc efekt nigdy nie stoi w miejscu, ale przyspiesza w głośnych fragmentach.
        public float WavelengthSpeed { get; set; } = 0.15f;

        // Liczba bloków losowanych w trybie Blocks przy każdym wykrytym beacie - więcej
        // bloków = drobniejszy, bardziej "poszatkowany" wzorzec kolorów na pasku.
        public int BlocksCount { get; set; } = 6;

        // Współczynnik wygładzania (EMA - exponential moving average) głośności dla trybu
        // Fade - bliżej 1.0 = wolniejsze, bardziej "oddychające" przejścia; bliżej 0.0 =
        // efekt zaczyna przypominać Energy (reaguje praktycznie natychmiast).
        public float FadeSmoothing { get; set; } = 0.85f;
    }
}
