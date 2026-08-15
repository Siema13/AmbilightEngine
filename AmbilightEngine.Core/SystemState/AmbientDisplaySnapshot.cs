namespace AmbilightEngine.Core.SystemState
{
    /// <summary>
    /// Zrzut stanu wyświetlania WLED zapisywany przez AppEngineHost przy wejściu
    /// w tryb ambientowy (blokada/uśpienie/bezczynność), używany do przywrócenia
    /// poprzedniego trybu po wybudzeniu - działa NIEZALEŻNIE od PipelineManager,
    /// czyli także wtedy, gdy Video Sync nigdy nie było uruchomione.
    /// </summary>
    public readonly struct AmbientDisplaySnapshot
    {
        public readonly DisplayMode Mode;
        public readonly int WledEffectId;
        public readonly int WledPaletteId;
        public readonly int WledSpeed;
        public readonly int WledIntensity;
        public readonly int WledBrightness;
        public readonly (byte R, byte G, byte B) WledPrimaryColor;
        public readonly (byte R, byte G, byte B) WledSecondaryColor;

        public AmbientDisplaySnapshot(
            DisplayMode mode,
            int wledEffectId,
            int wledPaletteId,
            int wledSpeed,
            int wledIntensity,
            int wledBrightness,
            (byte R, byte G, byte B) wledPrimaryColor,
            (byte R, byte G, byte B) wledSecondaryColor)
        {
            Mode = mode;
            WledEffectId = wledEffectId;
            WledPaletteId = wledPaletteId;
            WledSpeed = wledSpeed;
            WledIntensity = wledIntensity;
            WledBrightness = wledBrightness;
            WledPrimaryColor = wledPrimaryColor;
            WledSecondaryColor = wledSecondaryColor;
        }
    }
}