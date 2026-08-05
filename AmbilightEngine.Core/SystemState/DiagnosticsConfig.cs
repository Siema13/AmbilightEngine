namespace AmbilightEngine.Core.SystemState
{
    // Centralny przełącznik logowania diagnostycznego do pliku (ambilight_diag.log).
    // Domyślnie wyłączone - ustaw na true tylko podczas aktywnego debugowania problemów
    // z SystemStateWatcher (blokada ekranu/bezczynność).
    public static class DiagnosticsConfig
    {
        public static bool IsFileLoggingEnabled = false;
    }
}