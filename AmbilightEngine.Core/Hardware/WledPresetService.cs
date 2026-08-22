// ============================================================================
// PLIK: AmbilightEngine.Core/Hardware/WledPresetService.cs
// ============================================================================
//
// Odpowiada wyłącznie za odczyt i aktywację presetów/playlist WLED przez JSON
// API. Celowo NIE reużywa WledDdpNetworkSender (który odpowiada za strumień
// DDP czasu rzeczywistego) - to osobna, prosta odpowiedzialność HTTP/JSON,
// więc rozdzielenie jest zgodne z SRP i nie komplikuje istniejącej klasy
// wysyłającej ramki na żywo.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Models;

namespace AmbilightEngine.Core.Hardware
{
    public sealed class WledPresetService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };

        private readonly HttpClient httpClient;

        public WledPresetService(HttpClient? httpClient = null)
        {
            this.httpClient = httpClient ?? new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        /// <summary>
        /// Pobiera wszystkie presety i playlisty zapisane na urządzeniu WLED.
        /// Zwraca listę posortowaną po numerze presetu, gotową do wyświetlenia
        /// w UI bez dodatkowego przetwarzania.
        /// </summary>
        public async Task<List<WledPresetInfo>> GetPresetsAsync(
    string ipAddress,
    CancellationToken cancellationToken = default)
        {
            var results = new List<WledPresetInfo>();

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                return results;
            }

            try
            {
                string url = $"http://{ipAddress}/presets.json";
                using HttpResponseMessage response = await httpClient
                    .GetAsync(url, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine(
                        $"[DIAG] WledPresetService: HTTP {(int)response.StatusCode} podczas pobierania presets.json.");
                    return results;
                }

                string json = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Parsowanie "miękkie" przez JsonDocument - format presetów WLED nie jest
                // sztywno ustandaryzowany (preset może być dowolną komendą API, np. macro
                // z "ps" jako string "1~ 3~"), więc próba deserializacji bezpośrednio do
                // klasy wywala cały dokument przy jednym nietypowym wpisie. Tutaj każdy
                // wpis odczytujemy pole po polu, z fallbackiem, więc jeden zepsuty preset
                // nie blokuje odczytu pozostałych.
                using JsonDocument document = JsonDocument.Parse(json);

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, out int presetId) || presetId <= 0)
                    {
                        continue;
                    }

                    try
                    {
                        WledPresetInfo? info = ParsePresetEntry(presetId, property.Value);

                        if (info is not null)
                        {
                            results.Add(info);
                        }
                    }
                    catch (Exception entryEx)
                    {
                        Debug.WriteLine(
                            $"[DIAG] WledPresetService: pominięto preset {presetId} - błąd parsowania: {entryEx.Message}");
                    }
                }

                results.Sort((a, b) => a.PresetId.CompareTo(b.PresetId));

                Debug.WriteLine(
                    $"[DIAG] WledPresetService: wczytano {results.Count} presetów/playlist z urządzenia {ipAddress}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DIAG] WledPresetService: błąd pobierania presets.json: {ex.Message}");
            }

            return results;
        }

        private static WledPresetInfo? ParsePresetEntry(int presetId, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? name = element.TryGetProperty("n", out JsonElement nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            WledPlaylistInfo? playlist = null;

            if (element.TryGetProperty("playlist", out JsonElement playlistElement) &&
                playlistElement.ValueKind == JsonValueKind.Object)
            {
                playlist = new WledPlaylistInfo
                {
                    PresetSequence = ReadIntArray(playlistElement, "ps"),
                    DurationsTenthsOfSecond = ReadIntArray(playlistElement, "dur"),
                    TransitionTenthsOfSecond = ReadFlexibleInt(playlistElement, "transition"),
                    RepeatCount = ReadFlexibleInt(playlistElement, "repeat"),
                    EndPresetId = ReadFlexibleInt(playlistElement, "end")
                };
            }

            // Pomijamy wpisy bez nazwy i bez playlisty - to prawdopodobnie pusty slot
            // albo surowa komenda macro API, której nie próbujemy renderować w UI.
            if (string.IsNullOrWhiteSpace(name) && playlist is null)
            {
                return null;
            }

            return new WledPresetInfo
            {
                PresetId = presetId,
                Name = name,
                Playlist = playlist
            };
        }

        private static List<int> ReadIntArray(JsonElement parent, string propertyName)
        {
            var results = new List<int>();

            if (!parent.TryGetProperty(propertyName, out JsonElement arrayElement))
            {
                return results;
            }

            // "ps" w playliście bywa tablicą liczb, ale w macro-presetach "ps" może być
            // stringiem typu "1~ 3~" (cykl presetów) - tego przypadku nie próbujemy
            // rozwijać do listy, po prostu zwracamy pustą listę bez wyjątku.
            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (JsonElement item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int value))
                {
                    results.Add(value);
                }
                else if (item.ValueKind == JsonValueKind.String &&
                         int.TryParse(item.GetString(), out int parsedValue))
                {
                    results.Add(parsedValue);
                }
            }

            return results;
        }

        private static int? ReadFlexibleInt(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out JsonElement valueElement))
            {
                return null;
            }

            if (valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt32(out int number))
            {
                return number;
            }

            if (valueElement.ValueKind == JsonValueKind.String &&
                int.TryParse(valueElement.GetString(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

        /// <summary>
        /// Aktywuje wskazany preset (albo playlistę, WLED rozróżnia to
        /// wewnętrznie po tym samym numerze) przez POST /json/state.
        /// </summary>
        public async Task<bool> ActivatePresetAsync(
            string ipAddress,
            int presetId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || presetId <= 0)
            {
                return false;
            }

            try
            {
                string url = $"http://{ipAddress}/json/state";
                var payload = new { ps = presetId };
                string json = JsonSerializer.Serialize(payload);

                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await httpClient
                    .PostAsync(url, content, cancellationToken)
                    .ConfigureAwait(false);

                bool success = response.IsSuccessStatusCode;

                Debug.WriteLine(
                    $"[DIAG] WledPresetService: aktywacja presetu {presetId} na {ipAddress}, sukces={success}.");

                return success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DIAG] WledPresetService: błąd aktywacji presetu {presetId}: {ex.Message}");
                return false;
            }
        }
    }
}