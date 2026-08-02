using System;

namespace AmbilightEngine.Core.Models
{
    public sealed class AppProfile
    {
        public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = string.Empty;
        public string ExecutableFileName { get; set; } = string.Empty;
        public int Priority { get; set; } = 0;

        public int BrightnessPercent { get; set; } = 100;
        public double SaturationBoost { get; set; } = 1.0;
        public int SmoothingSpeedMs { get; set; } = 120;
        public int BlackCutoffThreshold { get; set; } = 8;
        public int ColorTemperatureKelvin { get; set; } = 6500;
        public double GammaValue { get; set; } = 2.2;

        public bool IsBuiltInDefault { get; set; } = false;

        public bool MatchesProcess(string processExeName)
        {
            if (string.IsNullOrWhiteSpace(processExeName) || string.IsNullOrWhiteSpace(ExecutableFileName))
                return false;

            return string.Equals(processExeName, ExecutableFileName, StringComparison.OrdinalIgnoreCase);
        }
    }
}