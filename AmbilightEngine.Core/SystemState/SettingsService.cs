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
                    return new AmbilightSettings();
                }

                string json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<AmbilightSettings>(json);
                return loaded ?? new AmbilightSettings();
            }
            catch (Exception)
            {
                // Jeśli plik jest uszkodzony lub nieczytelny, bezpiecznie wracamy do wartości domyślnych.
                return new AmbilightSettings();
            }
        }

        public void Save(AmbilightSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch (Exception)
            {
                // Błąd zapisu (np. brak uprawnień) nie powinien wywalić aplikacji.
            }
        }
    }
}