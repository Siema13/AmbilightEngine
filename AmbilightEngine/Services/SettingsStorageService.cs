using System;
using System.IO;
using System.Text.Json;
using AmbilightEngine.Models;

namespace AmbilightEngine.Services
{
    /// <summary>
    /// Odpowiada za trwały zapis i odczyt ustawień skrótów klawiszowych w pliku JSON
    /// w katalogu %LOCALAPPDATA%\AmbilightEngine\hotkeys.json.
    /// </summary>
    public sealed class SettingsStorageService
    {
        private readonly string settingsFilePath;
        private readonly JsonSerializerOptions serializerOptions;

        public SettingsStorageService()
        {
            var appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmbilightEngine");

            Directory.CreateDirectory(appDataFolder);

            settingsFilePath = Path.Combine(appDataFolder, "hotkeys.json");

            serializerOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };
        }

        public HotkeySettings LoadHotkeySettings()
        {
            try
            {
                if (!File.Exists(settingsFilePath))
                {
                    var defaults = HotkeySettings.CreateDefault();
                    SaveHotkeySettings(defaults);
                    return defaults;
                }

                var json = File.ReadAllText(settingsFilePath);
                var settings = JsonSerializer.Deserialize<HotkeySettings>(json, serializerOptions);

                return settings ?? HotkeySettings.CreateDefault();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Uszkodzony lub niedostępny plik konfiguracyjny nie powinien blokować startu aplikacji.
                System.Diagnostics.Debug.WriteLine($"Nie udało się wczytać hotkeys.json: {ex.Message}");
                return HotkeySettings.CreateDefault();
            }
        }

        public bool SaveHotkeySettings(HotkeySettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, serializerOptions);
                File.WriteAllText(settingsFilePath, json);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Nie udało się zapisać hotkeys.json: {ex.Message}");
                return false;
            }
        }
    }
}