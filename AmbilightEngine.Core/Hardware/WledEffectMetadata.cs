using System;
using System.Collections.Generic;

namespace AmbilightEngine.Core.Hardware
{
    public sealed class WledEffectMetadata
    {
        public string SpeedLabel { get; init; } = string.Empty;
        public string IntensityLabel { get; init; } = string.Empty;
        public string Custom1Label { get; init; } = string.Empty;
        public string Custom2Label { get; init; } = string.Empty;
        public string Custom3Label { get; init; } = string.Empty;
        public string Check1Label { get; init; } = string.Empty;
        public string Check2Label { get; init; } = string.Empty;
        public string Check3Label { get; init; } = string.Empty;

        public string Color1Label { get; init; } = string.Empty;
        public string Color2Label { get; init; } = string.Empty;
        public string Color3Label { get; init; } = string.Empty;
        public string PaletteLabel { get; init; } = string.Empty;

        public bool RequiresMatrix2D { get; init; }

        public IReadOnlyDictionary<string, string> Defaults { get; init; } = new Dictionary<string, string>();

        public bool HasCustom1 => !string.IsNullOrEmpty(Custom1Label);
        public bool HasCustom2 => !string.IsNullOrEmpty(Custom2Label);
        public bool HasCustom3 => !string.IsNullOrEmpty(Custom3Label);
        public bool HasCheck1 => !string.IsNullOrEmpty(Check1Label);
        public bool HasCheck2 => !string.IsNullOrEmpty(Check2Label);
        public bool HasCheck3 => !string.IsNullOrEmpty(Check3Label);
        public bool HasPalette => !string.IsNullOrEmpty(PaletteLabel);

        public static string ResolveLabel(string rawLabel, string defaultLabel)
        {
            if (string.IsNullOrEmpty(rawLabel)) return string.Empty;
            return rawLabel == "!" ? defaultLabel : rawLabel;
        }
    }

    public static class WledEffectMetadataParser
    {
        public static WledEffectMetadata Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new WledEffectMetadata();
            }

            int atIndex = raw.IndexOf('@');
            string afterName = atIndex >= 0 ? raw[(atIndex + 1)..] : raw;

            string[] segments = afterName.Split(';');

            string[] controlLabels = SplitOrEmpty(segments, 0);
            string[] colorLabels = SplitOrEmpty(segments, 1);
            string paletteLabel = segments.Length > 2 ? segments[2].Trim() : string.Empty;
            string flags = segments.Length > 3 ? segments[3].Trim() : string.Empty;
            string defaultsRaw = segments.Length > 4 ? segments[4].Trim() : string.Empty;

            string Get(string[] arr, int i) => i < arr.Length ? arr[i].Trim() : string.Empty;

            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(defaultsRaw))
            {
                foreach (string pair in defaultsRaw.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = pair[..eq].Trim();
                        string value = pair[(eq + 1)..].Trim();
                        if (!string.IsNullOrEmpty(key))
                        {
                            defaults[key] = value;
                        }
                    }
                }
            }

            return new WledEffectMetadata
            {
                SpeedLabel = WledEffectMetadata.ResolveLabel(Get(controlLabels, 0), "Prędkość"),
                IntensityLabel = WledEffectMetadata.ResolveLabel(Get(controlLabels, 1), "Intensywność"),
                Custom1Label = Get(controlLabels, 2) == "!" ? "Custom 1" : Get(controlLabels, 2),
                Custom2Label = Get(controlLabels, 3) == "!" ? "Custom 2" : Get(controlLabels, 3),
                Custom3Label = Get(controlLabels, 4) == "!" ? "Custom 3" : Get(controlLabels, 4),
                Check1Label = Get(controlLabels, 5) == "!" ? "Opcja 1" : Get(controlLabels, 5),
                Check2Label = Get(controlLabels, 6) == "!" ? "Opcja 2" : Get(controlLabels, 6),
                Check3Label = Get(controlLabels, 7) == "!" ? "Opcja 3" : Get(controlLabels, 7),
                Color1Label = WledEffectMetadata.ResolveLabel(Get(colorLabels, 0), "Kolor 1"),
                Color2Label = WledEffectMetadata.ResolveLabel(Get(colorLabels, 1), "Kolor 2"),
                Color3Label = WledEffectMetadata.ResolveLabel(Get(colorLabels, 2), "Kolor 3"),
                PaletteLabel = WledEffectMetadata.ResolveLabel(paletteLabel, "Paleta"),
                RequiresMatrix2D = flags.Contains('2'),
                Defaults = defaults
            };
        }

        private static string[] SplitOrEmpty(string[] segments, int index)
        {
            return index < segments.Length && !string.IsNullOrEmpty(segments[index])
                ? segments[index].Split(',')
                : Array.Empty<string>();
        }
    }
}