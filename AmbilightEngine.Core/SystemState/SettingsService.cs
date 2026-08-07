using System;
using System.IO;
using System.Text.Json;

namespace AmbilightEngine.Core.SystemState
{
    // Odpowiada wyłącznie za trwały zapis/odczyt konfiguracji na dysku.
    // Plik trafia do standardowego folderu AppData, żeby nie zaśmiecać folderu instalacyjnego.
    public sealed class SettingsService
    {
        private readonly string filePath;
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public SettingsService()
        {
            string appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmbilightEngine");

            Directory.CreateDirectory(appDataFolder);
            filePath = Path.Combine(appDataFolder, "settings.json");
        }

        public AmbilightSettings Load()
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] SettingsService: brak pliku ustawień, tworzę domyślne: {filePath}");

                    return new AmbilightSettings();
                }

                string json = File.ReadAllText(filePath);

                var loaded = JsonSerializer.Deserialize<AmbilightSettings>(json);

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] SettingsService: wczytano ustawienia z: {filePath}; " +
                    $"AutoStartAmbilight={loaded?.AutoStartAmbilight}");

                return loaded ?? new AmbilightSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] SettingsService: BŁĄD odczytu ustawień z '{filePath}': {ex}");

                return new AmbilightSettings();
            }
        }

        public void Save(AmbilightSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, jsonOptions);
                File.WriteAllText(filePath, json);

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] SettingsService: zapisano ustawienia do: {filePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] SettingsService: BŁĄD zapisu ustawień do '{filePath}': {ex}");
            }
        }
    }
}