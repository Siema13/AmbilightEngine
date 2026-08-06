using System;

namespace AmbilightEngine.Core.Models
{
    // Reprezentuje zestaw parametrów obrazu, który można zapisać ręcznie z ustawień
    // (np. "Kino wieczorem") albo powiązać automatycznie z konkretnym procesem (np. cs2.exe).
    public sealed class AppProfile
    {
        public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = string.Empty;

        // Pusty ExecutableFileName oznacza profil czysto manualny (bez auto-przełączania).
        public string ExecutableFileName { get; set; } = string.Empty;
        public int Priority { get; set; } = 0;

        public int BrightnessPercent { get; set; } = 100;
        public double SaturationBoost { get; set; } = 1.0;
        public int SmoothingSpeedMs { get; set; } = 120;
        public int BlackCutoffThreshold { get; set; } = 8;
        public int ColorTemperatureKelvin { get; set; } = 6500;

        public double GammaValue { get; set; } = 2.2;
        public bool IsBuiltInDefault { get; set; } = false;

        // Odróżnia profile zapisane ręcznie z ekranu ustawień obrazu od profili
        // auto-przełączanych przez ProcessProfileWatcher — potrzebne, żeby UI wiedziało,
        // które profile pokazywać na liście "Zapisane profile obrazu".
        public bool IsManualSnapshot { get; set; } = false;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public bool MatchesProcess(string processExeName)
        {
            if (string.IsNullOrWhiteSpace(processExeName) || string.IsNullOrWhiteSpace(ExecutableFileName))
                return false;

            return string.Equals(processExeName, ExecutableFileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}