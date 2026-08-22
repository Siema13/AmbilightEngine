using System;
using AmbilightEngine.Core.SystemState;

namespace AmbilightEngine.Core.Models
{
    /// <summary>
    /// Reprezentuje zapisaną "scenę" Quick Palette - kompletny, nazwany zrzut stanu wyświetlania
    /// (tryb + parametry Static Color / WLED Effect + Master Brightness + opcjonalny preset bieli),
    /// który użytkownik może błyskawicznie przywrócić z Dashboardu jednym kliknięciem.
    ///
    /// Model jest celowo niezależny od AppProfile: AppProfile odpowiada za automatyczne profile
    /// per-aplikacja (wyzwalane zmianą aktywnego procesu), a SceneProfile to ręcznie zapisany
    /// "snapshot" wyglądu LED, uruchamiany wyłącznie na żądanie użytkownika.
    /// </summary>
    public sealed class SceneProfile
    {
        // Generowany raz, przy tworzeniu instancji - ten sam wzorzec co AppProfile.ProfileId,
        // żeby zachować spójną konwencję identyfikatorów w całym projekcie.
        public string SceneId { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // Tryb wyświetlania, który scena ma przywrócić.
        public DisplayMode Mode { get; set; } = DisplayMode.StaticColor;

        // --- Static Color ---
        public byte StaticColorR { get; set; } = 255;
        public byte StaticColorG { get; set; } = 255;
        public byte StaticColorB { get; set; } = 255;

        // --- WLED Effect ---
        public int WledEffectId { get; set; }
        public int WledPaletteId { get; set; }
        public int WledSpeed { get; set; } = 128;
        public int WledIntensity { get; set; } = 128;

        // Bazowa jasność efektu WLED (0-255), NIEZALEŻNA od Master Brightness -
        // ta sama semantyka co AmbilightSettings.LastWledBrightness.
        public int WledBrightness { get; set; } = 128;

        public int WledCustom1 { get; set; } = 128;
        public int WledCustom2 { get; set; } = 128;
        public int WledCustom3 { get; set; }

        public bool WledCheck1 { get; set; }
        public bool WledCheck2 { get; set; }
        public bool WledCheck3 { get; set; }

        public byte WledPrimaryColorR { get; set; } = 255;
        public byte WledPrimaryColorG { get; set; } = 255;
        public byte WledPrimaryColorB { get; set; } = 255;

        public byte WledSecondaryColorR { get; set; }
        public byte WledSecondaryColorG { get; set; }
        public byte WledSecondaryColorB { get; set; }

        // --- Globalne modyfikatory opcjonalne ---
        // Zapisywane zawsze (0-100), żeby scena odtwarzała dokładnie ten sam efekt końcowy
        // na diodach, niezależnie od aktualnego globalnego ustawienia Master Brightness.
        public int MasterBrightnessPercent { get; set; } = 100;

        // Preset bieli jest opcjonalny - scena może nie ingerować w temperaturę barwową
        // aktywnego profilu ImageDsp (np. scena WLED Effect nie ma potrzeby ustawiać Kelvinów).
        public int? ColorTemperatureKelvin { get; set; }
    }
}
