using System.Collections.Generic;
using AmbilightEngine.Core.Models;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Models;

namespace AmbilightEngine.Core.SystemState
{
    public enum AmbientLightMode
    {
        Off,
        LoungeLight
    }

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

        // NOWOŚĆ: gdy true, tryb ambientowy aktywuje zapisany w WLED preset/playlistę
        // (PresetId) zamiast surowego efektu skonstruowanego z pól powyżej. Wartość
        // domyślna false gwarantuje, że istniejące konfiguracje wczytane ze starszego
        // settings.json (bez tych pól) zachowują dotychczasowe zachowanie oparte
        // na surowym efekcie WLED, bez żadnej migracji danych.
        public bool UsePreset { get; set; } = false;
        public int PresetId { get; set; } = 0;
    }

    public sealed class AmbilightSettings
    {
        public string? WallColorHex { get; set; } = null;
        public float WallColorStrength { get; set; } = 0.5f;

        public int WledEffectSpeed { get; set; } = 128;
        public int WledEffectIntensity { get; set; } = 128;

        // --- Połączenie i sprzęt ---
        public string EspIpAddress { get; set; } = "192.168.1.38";
        public int LedCount { get; set; } = 22;

        // --- Przetwarzanie obrazu ---
        public float SmoothingFactor { get; set; } = 0.3f;
        public int PixelSkipStep { get; set; } = 4;
        public int SamplingDepth { get; set; } = 80;

        private int edgeFeatherPixels = 2;
        public int EdgeFeatherPixels
        {
            get => edgeFeatherPixels;
            set => edgeFeatherPixels = value < 0 ? 0 : (value > 40 ? 40 : value);
        }

        private double phaseSmoothingStrength = 0.0;
        public double PhaseSmoothingStrength
        {
            get => phaseSmoothingStrength;
            set => phaseSmoothingStrength = value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
        }

        // --- Kalibracja per-kanał RGB: Gain (mnożnik) ---
        private double channelGainR = 1.0;
        public double ChannelGainR
        {
            get => channelGainR;
            set => channelGainR = value < 0.2 ? 0.2 : (value > 2.0 ? 2.0 : value);
        }

        private double channelGainG = 1.0;
        public double ChannelGainG
        {
            get => channelGainG;
            set => channelGainG = value < 0.2 ? 0.2 : (value > 2.0 ? 2.0 : value);
        }

        private double channelGainB = 1.0;
        public double ChannelGainB
        {
            get => channelGainB;
            set => channelGainB = value < 0.2 ? 0.2 : (value > 2.0 ? 2.0 : value);
        }

        // NOWOŚĆ: kalibracja per-kanał RGB - Gamma (koryguje nieliniowość w środkowych
        // tonach NIEZALEŻNIE per kanał, w przeciwieństwie do globalnej GammaValue profilu).
        // 1.0 = neutralne.
        private double channelGammaR = 1.0;
        public double ChannelGammaR
        {
            get => channelGammaR;
            set => channelGammaR = value < 0.3 ? 0.3 : (value > 3.0 ? 3.0 : value);
        }

        private double channelGammaG = 1.0;
        public double ChannelGammaG
        {
            get => channelGammaG;
            set => channelGammaG = value < 0.3 ? 0.3 : (value > 3.0 ? 3.0 : value);
        }

        private double channelGammaB = 1.0;
        public double ChannelGammaB
        {
            get => channelGammaB;
            set => channelGammaB = value < 0.3 ? 0.3 : (value > 3.0 ? 3.0 : value);
        }

        // NOWOŚĆ: kalibracja per-kanał RGB - Offset/Lift (przesuwa czernie kanału).
        // Koryguje sytuację, gdy dioda "czerwona" świeci lekko pomarańczowo nawet przy
        // zerowym sygnale wejściowym. 0.0 = neutralne. Zakres: -0.2..0.2 (znormalizowane).
        private double channelOffsetR = 0.0;
        public double ChannelOffsetR
        {
            get => channelOffsetR;
            set => channelOffsetR = value < -0.2 ? -0.2 : (value > 0.2 ? 0.2 : value);
        }

        private double channelOffsetG = 0.0;
        public double ChannelOffsetG
        {
            get => channelOffsetG;
            set => channelOffsetG = value < -0.2 ? -0.2 : (value > 0.2 ? 0.2 : value);
        }

        private double channelOffsetB = 0.0;
        public double ChannelOffsetB
        {
            get => channelOffsetB;
            set => channelOffsetB = value < -0.2 ? -0.2 : (value > 0.2 ? 0.2 : value);
        }

        // --- Tryb ambientowy ---
        public AmbientEffectConfig LockScreenAmbient { get; set; } = new();
        public AmbientEffectConfig IdleAmbient { get; set; } = new();

        private int idleTimeoutMinutes = 5;

        public int IdleTimeoutMinutes
        {
            get => idleTimeoutMinutes;
            set => idleTimeoutMinutes = value < 1 ? 1 : value;
        }

        // --- Legacy ---
        public AmbientLightMode LockScreenMode { get; set; } = AmbientLightMode.LoungeLight;
        public AmbientLightMode IdleMode { get; set; } = AmbientLightMode.LoungeLight;
        public byte LoungeColorR { get; set; } = 255;
        public byte LoungeColorG { get; set; } = 147;
        public byte LoungeColorB { get; set; } = 41;

        // --- Przechwytywanie ekranu ---
        public bool AutoStartWithDefaultMonitor { get; set; } = true;
        public string SelectedMonitorDeviceId { get; set; } = string.Empty;

        // --- MQTT ---
        public bool MqttEnabled { get; set; } = false;
        public string MqttHost { get; set; } = "127.0.0.1";
        public int MqttPort { get; set; } = 1883;
        public string MqttUsername { get; set; } = string.Empty;
        public string MqttPassword { get; set; } = string.Empty;
        public string MqttTopicPrefix { get; set; } = "ambilight";
        public string MqttClientId { get; set; } = "AmbilightEngine";
        public bool MqttRetainStatus { get; set; } = true;

        // --- Geometria ---
        public bool UseCustomZoneLayout { get; set; } = false;
        public int TopLedCount { get; set; } = 8;
        public int BottomLedCount { get; set; } = 8;
        public int LeftLedCount { get; set; } = 3;
        public int RightLedCount { get; set; } = 3;

        public StartCorner ZoneStartCorner { get; set; } = StartCorner.BottomLeft;
        public StripDirection ZoneStripDirection { get; set; } = StripDirection.Clockwise;
        public int ZoneShiftOffset { get; set; } = 0;

        public List<int> ExcludedLedIndices { get; set; } = new();

        // --- Black bars ---
        public bool EnableBlackBarDetection { get; set; } = false;
        public byte BlackBarThreshold { get; set; } = 18;
        public double BlackBarMinRatio { get; set; } = 0.92;

        // --- Motyw ---
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

        // --- Tryb wyświetlania ---
        public DisplayMode ActiveDisplayMode { get; set; } = DisplayMode.VideoSync;

        public byte StaticColorR { get; set; } = 255;
        public byte StaticColorG { get; set; } = 255;
        public byte StaticColorB { get; set; } = 255;

        // --- Preset temperatury światła (Static Color) ---
        // Pamięta pozycję w cyklu 2700K/4000K/5000K/6500K/9300K używanym przez skrót
        // globalny "whitepoint.cycle". W przeciwieństwie do AppProfile.ColorTemperatureKelvin
        // (który koryguje obraz z kamery w Video Sync), to pole steruje REALNYM kolorem
        // światła LED w trybie Static Color - jak przełącznik ciepłoty w lampie sufitowej.
        public int LastWhitePresetKelvin { get; set; } = 6500;
        public string SelectedWledEffectId { get; set; } = string.Empty;

        // --- Ostatni efekt WLED ---
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

        // --- Dynamika światła ---
        public double MotionAttackSpeed { get; set; } = 0.6;
        public double MotionDecaySpeed { get; set; } = 0.3;
        public double ColorSensitivity { get; set; } = 1.0;
        public byte MinimumBrightnessFloor { get; set; } = 5;

        private double zonePeakWeight = 0.3;
        public double ZonePeakWeight
        {
            get => zonePeakWeight;
            set => zonePeakWeight = value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);
        }

        private double shadowBoostStrength = 1.0;
        public double ShadowBoostStrength
        {
            get => shadowBoostStrength;
            set => shadowBoostStrength = value < 1.0 ? 1.0 : (value > 4.0 ? 4.0 : value);
        }

        public byte NoiseFloor { get; set; } = 4;

        public bool HasCompletedCalibrationOnboarding { get; set; } = false;

        // --- Master Brightness ---
        // Globalny mnożnik końcowej jasności dla Video Sync, Static Color i efektów WLED.
        // Nie zmienia BrightnessPercent profili ani LastWledBrightness efektu.
        private int masterBrightnessPercent = 100;

        public int MasterBrightnessPercent
        {
            get => masterBrightnessPercent;
            set => masterBrightnessPercent = value < 0 ? 0 : (value > 100 ? 100 : value);
        }

        // --- Zachowanie aplikacji ---
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimizedToTray { get; set; } = false;
        public bool CloseToTray { get; set; } = true;
        public bool OsdEnabled { get; set; } = true;
        public bool AutoStartAmbilight { get; set; } = false;
        public DisplayMode AutoStartDisplayMode { get; set; } = DisplayMode.VideoSync;

        // --- Shortcuts ---
        public HotkeySettings Hotkeys { get; set; } = HotkeySettings.CreateDefault();

        // --- Profile ---
        public List<AppProfile> Profiles { get; set; } = new();

        // --- Quick Palette: zapisane sceny ---
        // Każda scena to kompletny, nazwany zrzut stanu wyświetlania (tryb + parametry Static Color
        // lub WLED Effect + Master Brightness + opcjonalny preset bieli), uruchamiany na żądanie
        // z Dashboardu. Niezależne od Profiles (AppProfile), które są wyzwalane automatycznie
        // przez ProcessProfileWatcher na podstawie aktywnego procesu.
        public List<SceneProfile> Scenes { get; set; } = new();

        public AppProfile DefaultProfile { get; set; } = new()
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