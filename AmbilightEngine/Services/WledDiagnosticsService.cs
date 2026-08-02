using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AmbilightEngine.Services;

public sealed class WledDiagnosticsService : IWledDiagnosticsService
{
    private readonly HttpClient httpClient;

    public WledDiagnosticsService()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task<bool> TestConnectionAsync(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        try
        {
            string url = BuildJsonInfoUrl(ipAddress);
            using HttpResponseMessage response = await httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<WledDiagnosticsResult> GetDiagnosticsAsync(string ipAddress)
    {
        var result = new WledDiagnosticsResult();

        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            result.StatusText = "Brak adresu IP";
            return result;
        }

        try
        {
            string infoUrl = BuildJsonInfoUrl(ipAddress);
            string stateUrl = BuildJsonStateUrl(ipAddress);

            using HttpResponseMessage infoResponse = await httpClient.GetAsync(infoUrl);
            using HttpResponseMessage stateResponse = await httpClient.GetAsync(stateUrl);

            if (!infoResponse.IsSuccessStatusCode || !stateResponse.IsSuccessStatusCode)
            {
                result.StatusText = "Brak połączenia";
                return result;
            }

            string infoJson = await infoResponse.Content.ReadAsStringAsync();
            string stateJson = await stateResponse.Content.ReadAsStringAsync();

            using JsonDocument infoDoc = JsonDocument.Parse(infoJson);
            using JsonDocument stateDoc = JsonDocument.Parse(stateJson);

            result.IsReachable = true;
            result.StatusText = "Połączono";

            JsonElement infoRoot = infoDoc.RootElement;
            JsonElement stateRoot = stateDoc.RootElement;

            if (infoRoot.TryGetProperty("name", out JsonElement nameElement))
            {
                result.DeviceName = nameElement.GetString() ?? "-";
            }

            if (infoRoot.TryGetProperty("ver", out JsonElement versionElement))
            {
                result.Version = versionElement.GetString() ?? "-";
            }

            if (stateRoot.TryGetProperty("on", out JsonElement onElement))
            {
                result.PowerState = onElement.GetBoolean() ? "Włączone" : "Wyłączone";
            }

            if (stateRoot.TryGetProperty("bri", out JsonElement briElement))
            {
                result.Brightness = briElement.GetInt32().ToString();
            }

            if (infoRoot.TryGetProperty("leds", out JsonElement ledsElement) &&
                ledsElement.TryGetProperty("count", out JsonElement countElement))
            {
                result.LedInfo = countElement.GetInt32().ToString();
            }
        }
        catch
        {
            result.IsReachable = false;
            result.StatusText = "Błąd połączenia";
        }

        return result;
    }

    private static string BuildJsonInfoUrl(string ipAddress)
    {
        return $"http://{ipAddress}/json/info";
    }

    private static string BuildJsonStateUrl(string ipAddress)
    {
        return $"http://{ipAddress}/json/state";
    }
}