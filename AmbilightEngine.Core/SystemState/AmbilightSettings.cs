using System.Collections.Generic;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.SystemState
{
    public enum AmbientLightMode
    {
        Off,
        LoungeLight
    }

    // Kompletny, serializowalny kontener wszystkich ustawień aplikacji.
    // Właściwości muszą mieć publiczne get/set, żeby System.Text.Json mógł je zapisać i wczytać.
    public sealed class AmbilightSettings
    {
        public string EspIpAddress { get; set; } = "192.168.1.38";
        public int LedCount { get; set; } = 22;

        public float SmoothingFactor { get; set; } = 0.3f;
        public int PixelSkipStep { get; set; } = 4;
        public int SamplingDepth { get; set; } = 80;

        public AmbientLightMode LockScreenMode { get; set; } = AmbientLightMode.LoungeLight;
        public AmbientLightMode IdleMode { get; set; } = AmbientLightMode.LoungeLight;
        public int IdleTimeoutMinutes { get; set; } = 5;

        public byte LoungeColorR { get; set; } = 255;
        public byte LoungeColorG { get; set; } = 147;
        public byte LoungeColorB { get; set; } = 41;

        // Gdy true, aplikacja przy starcie Ambilighta automatycznie wybiera główny monitor systemowy
        // (przez MonitorCaptureHelper) i pomija natywne okno wyboru GraphicsCapturePicker.
        // Gdy false, użytkownik przy każdym starcie musi ręcznie wybrać monitor/okno do przechwycenia.
        public bool AutoStartWithDefaultMonitor { get; set; } = true;

        // --- Kreator geometrii stref LED ---

        // Gdy true, ZoneMapGenerator używa jawnego podziału per-bok (TopLedCount itd.) zamiast
        // automatycznego, proporcjonalnego rozkładu na podstawie samego LedCount.
        public bool UseCustomZoneLayout { get; set; } = false;

        public int TopLedCount { get; set; } = 8;
        public int BottomLedCount { get; set; } = 8;
        public int LeftLedCount { get; set; } = 3;
        public int RightLedCount { get; set; } = 3;

        public StartCorner ZoneStartCorner { get; set; } = StartCorner.BottomLeft;
        public StripDirection ZoneStripDirection { get; set; } = StripDirection.Clockwise;

        // Przesunięcie fizycznego mapowania o N diod - kompensuje niedokładne ułożenie paska LED
        // względem wygenerowanej geometrii (np. kabel zasilający wymusił inny punkt startowy).
        public int ZoneShiftOffset { get; set; } = 0;

        // Indeksy diod fizycznie uszkodzonych lub pominiętych w układzie - zostają wygaszone na czarno.
        public List<int> ExcludedLedIndices { get; set; } = new List<int>();
    }
}