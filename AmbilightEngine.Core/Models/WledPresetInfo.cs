// ============================================================================
// PLIK: AmbilightEngine.Core/Models/WledPresetInfo.cs
// ============================================================================
//
// Modele odpowiedzi z endpointu WLED GET /presets.json. Klucze najwyższego
// poziomu w JSON to numery presetów jako string (np. "1", "12"), dlatego
// deserializujemy do Dictionary<string, WledPresetInfo> zamiast listy -
// WLED nie gwarantuje ciągłości numeracji (usunięty preset zostawia dziurę).

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AmbilightEngine.Core.Models
{
    /// <summary>
    /// Pojedynczy wpis z /presets.json. Może to być zwykły preset (zapisany
    /// stan LED) albo playlista (obecność Playlist != null). WLED miesza
    /// oba typy w tym samym pliku, rozróżniane wyłącznie obecnością pola "playlist".
    /// </summary>
    public sealed class WledPresetInfo
    {
        [JsonPropertyName("n")]
        public string? Name { get; set; }

        [JsonPropertyName("on")]
        public bool? IsOn { get; set; }

        [JsonPropertyName("bri")]
        public int? Brightness { get; set; }

        [JsonPropertyName("playlist")]
        public WledPlaylistInfo? Playlist { get; set; }

        // Pole pomocnicze wypełniane ręcznie po deserializacji - WLED nie
        // zwraca numeru presetu wewnątrz obiektu, tylko jako klucz słownika,
        // więc UI potrzebuje tej wartości skopiowanej "do środka" obiektu,
        // żeby dało się go bindować jako pojedynczy element listy w XAML.
        [JsonIgnore]
        public int PresetId { get; set; }

        [JsonIgnore]
        public bool IsPlaylist => Playlist != null;

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name)
            ? $"Preset {PresetId}"
            : Name;

        // Etykieta gotowa do wyświetlenia w ComboBox (DisplayMemberPath) -
        // odróżnia playlisty od zwykłych presetów bez dodatkowego konwertera.
        [JsonIgnore]
        public string DisplayLabel => IsPlaylist
            ? $"{DisplayName} (playlista, {Playlist!.PresetSequence.Count} kroków)"
            : DisplayName;
    }

    /// <summary>
    /// Definicja playlisty osadzonej wewnątrz presetu WLED. "PresetSequence" to
    /// kolejność numerów presetów do odtworzenia, "DurationsTenthsOfSecond" to
    /// czas trwania każdego kroku w dziesiątych częściach sekundy.
    /// </summary>
    public sealed class WledPlaylistInfo
    {
        [JsonPropertyName("ps")]
        public List<int> PresetSequence { get; set; } = new();

        [JsonPropertyName("dur")]
        public List<int> DurationsTenthsOfSecond { get; set; } = new();

        [JsonPropertyName("transition")]
        [JsonConverter(typeof(FlexibleNullableIntConverter))]
        public int? TransitionTenthsOfSecond { get; set; }

        [JsonPropertyName("repeat")]
        [JsonConverter(typeof(FlexibleNullableIntConverter))]
        public int? RepeatCount { get; set; }

        [JsonPropertyName("end")]
        [JsonConverter(typeof(FlexibleNullableIntConverter))]
        public int? EndPresetId { get; set; }
    }
}