using System.Collections.Generic;

namespace AmbilightEngine.Models
{
    /// <summary>
    /// Znane identyfikatory akcji domenowych sterowanych skrótami globalnymi.
    /// Trzymane jako stałe stringi, żeby ułatwić serializację JSON i uniknąć magic numbers.
    /// </summary>
    public static class HotkeyActionIds
    {
        public const string ToggleEngine = "engine.toggle";
        public const string CycleMode = "mode.cycle";
        public const string BrightnessUp = "brightness.up";
        public const string BrightnessDown = "brightness.down";
        public const string Blackout = "engine.blackout";
        public const string CycleWhitePreset = "whitepoint.cycle";
    }

    /// <summary>
    /// Kontener wszystkich przypisań skrótów zapisywany do pliku konfiguracyjnego JSON.
    /// </summary>
    public sealed class HotkeySettings
    {
        public List<HotkeyBinding> Bindings { get; set; } = new();

        /// <summary>
        /// Tworzy domyślną konfigurację bez żadnych przypisanych skrótów (zgodnie z wymaganiem: brak domyślnych).
        /// </summary>
        public static HotkeySettings CreateDefault()
        {
            var settings = new HotkeySettings();

            foreach (var actionId in new[]
            {
                HotkeyActionIds.ToggleEngine,
                HotkeyActionIds.CycleMode,
                HotkeyActionIds.BrightnessUp,
                HotkeyActionIds.BrightnessDown,
                HotkeyActionIds.Blackout,
                HotkeyActionIds.CycleWhitePreset
            })
            {
                settings.Bindings.Add(new HotkeyBinding(actionId, modifiers: 0, virtualKey: 0));
            }

            return settings;
        }
    }
}