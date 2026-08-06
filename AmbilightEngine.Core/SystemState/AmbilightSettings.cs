using System.Collections.Generic;
using AmbilightEngine.Core.Models;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.SystemState
{
    public enum AmbientLightMode
    {
        Off,
        LoungeLight
    }

    // Konfiguracja efektu WLED wywoływanego jednorazowo przy wejściu w tryb ambientowy
    // (blokada ekranu lub bezczynność). Zamiast ciągłego wysyłania DDP, wywołujemy natywny
    // efekt firmware WLED przez JSON API - eliminuje to problemy z timeoutem realtime.
    public sealed class AmbientEffectConfig
    {
        public bool IsEnabled { get; set; } = false;
        public int EffectId { get; set; } = 0;
        public int PaletteId { get; set; } = 0;
        public int Speed { get; set; } = 128;
        public int Intensity { get; set; } = 128;
        public int Brightness { get; set; } = 128;
        public byte PrimaryColorR { get; set; } = 255;
        public byte PrimaryColorG { get; set; } = 255;
        public byte PrimaryColorB { get; set; } = 255;
        public byte SecondaryColorR { get; set; } = 0;
        public byte SecondaryColorG { get; set; } = 0;
        public byte SecondaryColorB { get; set; } = 0;
    }

    public sealed class AmbilightSettings
    {
        /// <summary>Barwa ściany jako HEX (#RRGGBB). Null = kompensacja wyłączona.</summary>
        public string? WallColorHex { get; set; } = null;

        /// <summary>Siła kompensacji barwy ściany [0.0 – 1.0]. 0 = wyłączona, 1 = pełna.</summary>
        public float WallColorStrength { get; set; } = 0.5f;
        public int WledEffectSpeed { get; set; } = 128;
        public int WledEffectIntensity { get; set; } = 128;
        // --- Polaczenie i sprzet ---
        public string EspIpAddress { get; set; } = "192.168.1.38";
        public int LedCount { get; set; } = 22;

        // --- Przetwarzanie obrazu (podstawowe) ---
        public float SmoothingFactor { get; set; } = 0.3f;
        public int PixelSkipStep { get; set; } = 4;
        public int SamplingDepth { get; set; } = 80;

        // --- Tryb ambientowy (bezczynnosc / ekran blokady) ---
        // NOWOŚĆ: osobna konfiguracja efektu WLED per tryb, wywoływana jednorazowo przez
        // JSON API (zamiast ciągłego DDP). IsEnabled=false odpowiada dawnemu "Off".
        public AmbientEffectConfig LockScreenAmbient { get; set; } = new AmbientEffectConfig();
        public AmbientEffectConfig IdleAmbient { get; set; } = new AmbientEffectConfig();
        public int IdleTimeoutMinutes { get; set; } = 5;

        // --- LEGACY: pozostawione dla kompatybilności ze starym UI Ustawień.
        // Nowa logika (PipelineManager.EnterAmbientMode) już z nich nie korzysta -
        // zostaną usunięte po migracji strony ustawień na AmbientEffectConfig.
        public AmbientLightMode LockScreenMode { get; set; } = AmbientLightMode.LoungeLight;
        public AmbientLightMode IdleMode { get; set; } = AmbientLightMode.LoungeLight;
        public byte LoungeColorR { get; set; } = 255;
        public byte LoungeColorG { get; set; } = 147;
        public byte LoungeColorB { get; set; } = 41;

        // --- Przechwytywanie ekranu ---
        public bool AutoStartWithDefaultMonitor { get; set; } = true;
        public string SelectedMonitorDeviceId { get; set; } = string.Empty;

        // --- Integracja MQTT ---
        public bool MqttEnabled { get; set; } = false;
        public string MqttHost { get; set; } = "127.0.0.1";
        public int MqttPort { get; set; } = 1883;
        public string MqttUsername { get; set; } = string.Empty;
        public string MqttPassword { get; set; } = string.Empty;
        public string MqttTopicPrefix { get; set; } = "ambilight";
        public string MqttClientId { get; set; } = "AmbilightEngine";
        public bool MqttRetainStatus { get; set; } = true;

        // --- Ukladu stref (geometria) ---
        public bool UseCustomZoneLayout { get; set; } = false;

        public bool EnableBlackBarDetection { get; set; } = false;
        public string AccentThemeName { get; set; } = "Blue";
        public bool UseCustomTheme { get; set; } = false;

        public byte CustomAccentR { get; set; } = 0;
        public byte CustomAccentG { get; set; } = 103;
        public byte CustomAccentB { get; set; } = 192;

        public byte CustomWindowBackgroundR { get; set; } = 15;
        public byte CustomWindowBackgroundG { get; set; } = 14;
        public byte CustomWindowBackgroundB { get; set; } = 19;

        public byte CustomContentBackgroundR { get; set; } = 24;
        public byte CustomContentBackgroundG { get; set; } = 22;
        public byte CustomContentBackgroundB { get; set; } = 31;

        public byte CustomCardSurfaceR { get; set; } = 44;
        public byte CustomCardSurfaceG { get; set; } = 40;
        public byte CustomCardSurfaceB { get; set; } = 53;

        public byte CustomBackgroundAccentR { get; set; } = 214;
        public byte CustomBackgroundAccentG { get; set; } = 96;
        public byte CustomBackgroundAccentB { get; set; } = 52;

        public double UiGlassOpacity { get; set; } = 0.28;
        public string CustomBackgroundStyle { get; set; } = "SoftGradient";
        // --- Tryb wyswietlania ---
        public DisplayMode ActiveDisplayMode { get; set; } = DisplayMode.VideoSync;
        public byte StaticColorR { get; set; } = 255;
        public byte StaticColorG { get; set; } = 255;
        public byte StaticColorB { get; set; } = 255;
        public string SelectedWledEffectId { get; set; } = string.Empty;

        // --- Zapamiętane parametry ostatniego wywołanego efektu WLED (Dashboard) -
        // potrzebne do prawidłowego przywrócenia trybu WledEffects po wyjściu z ambientu.
        public int LastWledEffectId { get; set; } = 0;
        public int LastWledPaletteId { get; set; } = 0;
        public int LastWledSpeed { get; set; } = 128;
        public int LastWledIntensity { get; set; } = 128;
        public int LastWledBrightness { get; set; } = 128;
        public byte LastWledPrimaryColorR { get; set; } = 255;
        public byte LastWledPrimaryColorG { get; set; } = 255;
        public byte LastWledPrimaryColorB { get; set; } = 255;
        public byte LastWledSecondaryColorR { get; set; } = 0;
        public byte LastWledSecondaryColorG { get; set; } = 0;
        public byte LastWledSecondaryColorB { get; set; } = 0;
        public int LastWledCustom1 { get; set; } = 128;
        public int LastWledCustom2 { get; set; } = 128;
        public int LastWledCustom3 { get; set; } = 0;
        public bool LastWledCheck1 { get; set; }
        public bool LastWledCheck2 { get; set; }
        public bool LastWledCheck3 { get; set; }

        // --- Dynamika swiatla (attack/decay/sensitivity/floor) ---
        public double MotionAttackSpeed { get; set; } = 0.6;
        public double MotionDecaySpeed { get; set; } = 0.3;
        public double ColorSensitivity { get; set; } = 1.0;
        public byte MinimumBrightnessFloor { get; set; } = 5;

        // --- Geometria stref niestandardowych ---
        public int TopLedCount { get; set; } = 8;
        public int BottomLedCount { get; set; } = 8;
        public int LeftLedCount { get; set; } = 3;
        public int RightLedCount { get; set; } = 3;

        public StartCorner ZoneStartCorner { get; set; } = StartCorner.BottomLeft;
        public StripDirection ZoneStripDirection { get; set; } = StripDirection.Clockwise;
        public int ZoneShiftOffset { get; set; } = 0;

        public List<int> ExcludedLedIndices { get; set; } = new();

        // --- Zachowanie aplikacji ---
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimizedToTray { get; set; } = false;
        public bool CloseToTray { get; set; } = true;

        // --- Profile per-aplikacja ---
        public List<AppProfile> Profiles { get; set; } = new();

        public AppProfile DefaultProfile { get; set; } = new AppProfile
        {
            DisplayName = "Domyślny",
            IsBuiltInDefault = true,
            BrightnessPercent = 100,
            SaturationBoost = 1.0,
            SmoothingSpeedMs = 120,
            BlackCutoffThreshold = 8,
            ColorTemperatureKelvin = 6500,
            GammaValue = 2.2
        };
    }
}