using System;

namespace AmbilightEngine.Models
{
    /// <summary>
    /// Reprezentuje pojedyncze powiązanie akcji domenowej z globalnym skrótem klawiszowym.
    /// Modyfikatory przechowywane jako flagi Win32 (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).
    /// </summary>
    public sealed class HotkeyBinding
    {
        public string ActionId { get; set; } = string.Empty;

        public uint Modifiers { get; set; }

        public uint VirtualKey { get; set; }

        public bool IsAssigned => VirtualKey != 0;

        public HotkeyBinding()
        {
        }

        public HotkeyBinding(string actionId, uint modifiers, uint virtualKey)
        {
            ActionId = actionId;
            Modifiers = modifiers;
            VirtualKey = virtualKey;
        }

        /// <summary>
        /// Generuje czytelny opis skrótu do wyświetlenia w UI, np. "Ctrl+Shift+B".
        /// </summary>
        public string ToDisplayString()
        {
            if (!IsAssigned)
            {
                return "Brak";
            }

            var parts = new System.Collections.Generic.List<string>();

            const uint MOD_ALT = 0x0001;
            const uint MOD_CONTROL = 0x0002;
            const uint MOD_SHIFT = 0x0004;
            const uint MOD_WIN = 0x0008;

            if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");

            parts.Add(VirtualKeyToString(VirtualKey));

            return string.Join("+", parts);
        }

        private static string VirtualKeyToString(uint vk)
        {
            // Litery A-Z (0x41-0x5A) i cyfry 0-9 (0x30-0x39) mapują się bezpośrednio na kod ASCII.
            if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39)
            {
                return ((char)vk).ToString();
            }

            // Klawisze funkcyjne F1-F24 (0x70-0x87).
            if (vk is >= 0x70 and <= 0x87)
            {
                return $"F{vk - 0x6F}";
            }

            return $"VK_{vk:X2}";
        }
    }
}