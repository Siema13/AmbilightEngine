namespace AmbilightEngine.Core.Audio
{
    // Tryby generatora efektów audio-reaktywnych, wzorowane na najpopularniejszych efektach
    // z LedFx (https://github.com/LedFx/LedFx) - każdy mapuje inny aspekt AudioSpectrumFrame
    // na kolory diod.
    public enum AudioReactiveMode
    {
        // Cały pasek LED pulsuje jasnością/kolorem zgodnie z ogólną głośnością (RMS) -
        // odpowiednik klasycznego "VU meter" analogowego.
        VuMeter,

        // Pasek LED traktowany jako oś częstotliwości - jeden koniec to bas, drugi to wysokie
        // tony, każda dioda dostaje kolor/jasność zależną od energii w "swoim" fragmencie widma.
        SpectrumBar,

        // Pasek pulsuje krótkim, jaskrawym błyskiem w rytm wykrytych uderzeń basu (beat),
        // z płynnym zgaszeniem między uderzeniami - odpowiednik efektu "Beat" w LedFx.
        BeatPulse,

        // Odpowiednik LedFx "Energy" - cały pasek pulsuje jasnością do RMS, ale kolor
        // pochodzi z gradientu Primary->Secondary zamiast jednego stałego koloru.
        Energy,

        // Odpowiednik LedFx "Scroll"/"Wave" - kolor przesuwa się wzdłuż paska w rytm
        // wykrytych uderzeń basu, z płynnym, stałym ruchem między uderzeniami.
        Wave,

        // Odpowiednik LedFx "Bass strobe" - krótki, jaskrawy błysk konfigurowalnego koloru
        // WYŁĄCZNIE na uderzeniu basu, na tle koloru wtórnego (lub czerni) - w przeciwieństwie
        // do BeatPulse nie używa jasności RMS, tylko binarnego wykrycia beatu.
        BassStrobe,

        // Odpowiednik LedFx "Multi-color spectrum" - pasek dzielony na trzy segmenty
        // (bas/mid/treble), każdy w osobnym, konfigurowalnym kolorze, z jasnością segmentu
        // zależną od energii odpowiadającego mu pasma częstotliwości.
        MultiColorSpectrum,

        // Odpowiednik LedFx "Bar" - pasek wypełnia się od jednego krańca proporcjonalnie do
        // RMS (jak analogowy wskaźnik poziomu), plus osobny, szybko narastający i wolno
        // opadający "peak indicator" (jedna dioda) pokazujący ostatni szczyt głośności.
        Bar,

        // Odpowiednik LedFx "Scroll" - przy każdym wykrytym beacie nowy "pakiet" koloru
        // (zależny od dominującego pasma: bas/mid/treble) jest wstrzykiwany na start paska
        // i przewijany wzdłuż całej jego długości w czasie, tworząc ciąg przesuwających się
        // impulsów zamiast jednego pojedynczego bloku (jak w Wave).
        Scroll,

        // Odpowiednik LedFx "Fade" - cały pasek płynnie "oddycha" jasnością i odcieniem
        // gradientu Primary->Secondary zgodnie ze WYGŁADZONĄ (a nie chwilową) głośnością,
        // dając miękki, powolny efekt bez ostrych przejść - w przeciwieństwie do Energy.
        Fade,

        // Odpowiednik LedFx "Blocks" - pasek dzielony na kilka bloków o losowej szerokości
        // i losowo przydzielonym kolorze (z palety Primary/Secondary/Bass/Mid/Treble),
        // przelosowywanych przy każdym wykrytym beacie - daje wrażenie rytmicznego "tasowania"
        // kolorów w takt muzyki.
        Blocks,

        // Odpowiednik LedFx "Wavelength" - pełny gradient tęczowy (HSV) rozciągnięty na całej
        // długości paska, przesuwający się w czasie ze stałą prędkością bazową, przyspieszany
        // dodatkowo przez chwilową głośność (RMS) - im głośniej, tym szybszy przepływ kolorów.
        Wavelength
    }
}
