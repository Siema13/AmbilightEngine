using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AmbilightEngine.Core.Models
{
    // Określa, co profil robi po aktywacji: standardowa zmiana parametrów obrazu
    // (Video Sync), wymuszenie stałego koloru LED, wymuszenie konkretnego efektu
    // WLED, albo aktywacja zapisanego w aplikacji webowej WLED presetu/playlisty.
    // Umożliwia scenariusze typu "gdy uruchamiam TeamSpeak, diody mają zaświecić
    // stałym niebieskim kolorem", niezależnie od bieżącego trybu wyświetlania
    // Video Sync.
    public enum ProfileActionType
    {
        ImageDsp,
        StaticColor,
        WledEffect,
        WledPreset
    }

    public sealed class AppProfile : INotifyPropertyChanged
    {
        private string profileId = Guid.NewGuid().ToString("N");
        private string displayName = string.Empty;
        private string executableFileName = string.Empty;
        private bool allowBackgroundActivation;
        private int priority;

        private int brightnessPercent = 100;
        private double saturationBoost = 1.0;
        private int smoothingSpeedMs = 120;
        private int blackCutoffThreshold = 8;
        private int colorTemperatureKelvin = 6500;
        private double gammaValue = 2.2;

        private bool isBuiltInDefault;
        private bool isManualSnapshot;
        private DateTime createdAtUtc = DateTime.UtcNow;

        // NOWOŚĆ: typ akcji profilu i parametry dla trybów innych niż ImageDsp.
        // Wartości domyślne (ImageDsp / białe światło / efekt 0) gwarantują, że
        // istniejące profile wczytane ze starszego pliku ustawień JSON (bez tych
        // pól) zachowują dotychczasowe zachowanie bez żadnej migracji danych.
        private ProfileActionType actionType = ProfileActionType.ImageDsp;

        private byte staticColorR = 255;
        private byte staticColorG = 255;
        private byte staticColorB = 255;

        private int wledEffectId;
        private int wledPaletteId;
        private int wledEffectSpeed = 128;
        private int wledEffectIntensity = 128;
        private int wledEffectBrightness = 128;
        private byte wledPrimaryColorR = 255;
        private byte wledPrimaryColorG = 255;
        private byte wledPrimaryColorB = 255;
        private byte wledSecondaryColorR;
        private byte wledSecondaryColorG;
        private byte wledSecondaryColorB;

        // NOWOŚĆ: numer presetu/playlisty WLED do aktywacji, gdy ActionType == WledPreset.
        // Odpowiada numerowi widocznemu w aplikacji webowej WLED (zakładka Presets),
        // np. 3 dla presetu "Startup". Wartość domyślna 0 oznacza "nie wybrano".
        private int wledPresetId;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProfileId
        {
            get => profileId;
            set => SetProperty(ref profileId, value);
        }

        public string DisplayName
        {
            get => displayName;
            set => SetProperty(ref displayName, value);
        }

        public string ExecutableFileName
        {
            get => executableFileName;
            set => SetProperty(ref executableFileName, value);
        }

        // Gdy false, profil działa wyłącznie dla aktywnego okna.
        // Gdy true, profil może zostać wybrany także dla procesu w tle.
        public bool AllowBackgroundActivation
        {
            get => allowBackgroundActivation;
            set => SetProperty(ref allowBackgroundActivation, value);
        }

        public int Priority
        {
            get => priority;
            set => SetProperty(ref priority, value);
        }

        public int BrightnessPercent
        {
            get => brightnessPercent;
            set => SetProperty(ref brightnessPercent, value);
        }

        public double SaturationBoost
        {
            get => saturationBoost;
            set => SetProperty(ref saturationBoost, value);
        }

        public int SmoothingSpeedMs
        {
            get => smoothingSpeedMs;
            set => SetProperty(ref smoothingSpeedMs, value);
        }

        public int BlackCutoffThreshold
        {
            get => blackCutoffThreshold;
            set => SetProperty(ref blackCutoffThreshold, value);
        }

        public int ColorTemperatureKelvin
        {
            get => colorTemperatureKelvin;
            set => SetProperty(ref colorTemperatureKelvin, value);
        }

        public double GammaValue
        {
            get => gammaValue;
            set => SetProperty(ref gammaValue, value);
        }

        public bool IsBuiltInDefault
        {
            get => isBuiltInDefault;
            set => SetProperty(ref isBuiltInDefault, value);
        }

        public bool IsManualSnapshot
        {
            get => isManualSnapshot;
            set => SetProperty(ref isManualSnapshot, value);
        }

        public DateTime CreatedAtUtc
        {
            get => createdAtUtc;
            set => SetProperty(ref createdAtUtc, value);
        }

        // NOWOŚĆ: typ akcji wykonywanej przy aktywacji profilu.
        public ProfileActionType ActionType
        {
            get => actionType;
            set
            {
                if (SetProperty(ref actionType, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActionTypeImageDsp)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActionTypeStaticColor)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActionTypeWledEffect)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActionTypeWledPreset)));
                }
            }
        }

        public byte StaticColorR
        {
            get => staticColorR;
            set => SetProperty(ref staticColorR, value);
        }

        public byte StaticColorG
        {
            get => staticColorG;
            set => SetProperty(ref staticColorG, value);
        }

        public byte StaticColorB
        {
            get => staticColorB;
            set => SetProperty(ref staticColorB, value);
        }

        public int WledEffectId
        {
            get => wledEffectId;
            set => SetProperty(ref wledEffectId, value);
        }

        public int WledPaletteId
        {
            get => wledPaletteId;
            set => SetProperty(ref wledPaletteId, value);
        }

        public int WledEffectSpeed
        {
            get => wledEffectSpeed;
            set => SetProperty(ref wledEffectSpeed, value);
        }

        public int WledEffectIntensity
        {
            get => wledEffectIntensity;
            set => SetProperty(ref wledEffectIntensity, value);
        }

        public int WledEffectBrightness
        {
            get => wledEffectBrightness;
            set => SetProperty(ref wledEffectBrightness, value);
        }

        public byte WledPrimaryColorR
        {
            get => wledPrimaryColorR;
            set => SetProperty(ref wledPrimaryColorR, value);
        }

        public byte WledPrimaryColorG
        {
            get => wledPrimaryColorG;
            set => SetProperty(ref wledPrimaryColorG, value);
        }

        public byte WledPrimaryColorB
        {
            get => wledPrimaryColorB;
            set => SetProperty(ref wledPrimaryColorB, value);
        }

        public byte WledSecondaryColorR
        {
            get => wledSecondaryColorR;
            set => SetProperty(ref wledSecondaryColorR, value);
        }

        public byte WledSecondaryColorG
        {
            get => wledSecondaryColorG;
            set => SetProperty(ref wledSecondaryColorG, value);
        }

        public byte WledSecondaryColorB
        {
            get => wledSecondaryColorB;
            set => SetProperty(ref wledSecondaryColorB, value);
        }

        // NOWOŚĆ: numer presetu/playlisty WLED do aktywacji dla ActionType.WledPreset.
        public int WledPresetId
        {
            get => wledPresetId;
            set => SetProperty(ref wledPresetId, value);
        }

        // NOWOŚĆ: właściwości pomocnicze tylko do odczytu dla bindowania XAML
        // (RadioButton.IsChecked, StackPanel.Visibility) - unikają migotania/resetu
        // przy recyklingu DataTemplate, bo nie ma tu żadnego SelectedIndex do
        // odtworzenia przy każdym Loaded, tylko czysty odczyt aktualnego ActionType.
        public bool IsActionTypeImageDsp => ActionType == ProfileActionType.ImageDsp;
        public bool IsActionTypeStaticColor => ActionType == ProfileActionType.StaticColor;
        public bool IsActionTypeWledEffect => ActionType == ProfileActionType.WledEffect;
        public bool IsActionTypeWledPreset => ActionType == ProfileActionType.WledPreset;

        public bool MatchesProcess(string processExeName)
        {
            if (string.IsNullOrWhiteSpace(processExeName) ||
                string.IsNullOrWhiteSpace(ExecutableFileName))
            {
                return false;
            }

            return string.Equals(
                processExeName,
                ExecutableFileName,
                StringComparison.OrdinalIgnoreCase);
        }

        private bool SetProperty<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private string assignedHotkeyLabel = "Brak przypisanego skrótu";

        public string AssignedHotkeyLabel
        {
            get => assignedHotkeyLabel;
            set => SetProperty(ref assignedHotkeyLabel, value);
        }
    }
}