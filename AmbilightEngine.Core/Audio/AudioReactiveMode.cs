namespace AmbilightEngine.Core.Audio
{
    // Tryby generatora efektów audio-reaktywnych, wzorowane na najpopularniejszych efektach
    // z LedFx - każdy mapuje inny aspekt AudioSpectrumFrame na kolory diod.
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
        BeatPulse
    }
}
