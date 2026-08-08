using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AmbilightEngine.Core.Models
{
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

        private void SetProperty<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }
}