using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AmbilightEngine.Core.Processing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace AmbilightEngine.Core.Hardware
{
    public sealed class WledDdpNetworkSender : IOutputDevice
    {
        private const int MaxLedsPerPacket = 480;

        private readonly IPEndPoint endPoint;
        private readonly string httpBaseUrl;
        private readonly string effectsListUrl;
        private readonly string palettesListUrl;
        private readonly HttpClient httpClient;

        private UdpClient? udpClient;
        private byte[] packetBuffer = Array.Empty<byte>();
        private int currentLedCount;
        private bool isDisposed;

        public bool IsConnected => udpClient != null;

        public int LedCount => currentLedCount;

        public WledDdpNetworkSender(string ipAddress, int initialLedCount)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress, out IPAddress? ip))
                throw new ArgumentException("Nieprawidłowy adres IP ESP32/WLED.");

            endPoint = new IPEndPoint(ip, 4048);
            httpBaseUrl = $"http://{ipAddress}/json/state";
            effectsListUrl = $"http://{ipAddress}/json/eff";
            palettesListUrl = $"http://{ipAddress}/json/pal";

            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(800)
            };

            Reconfigure(initialLedCount);
        }

        public void Reconfigure(int newLedCount)
        {
            if (newLedCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(newLedCount));

            currentLedCount = newLedCount;

            int maxBufferSize = 10 + (MaxLedsPerPacket * 3);
            packetBuffer = new byte[maxBufferSize];

            packetBuffer[0] = 0x41;
            packetBuffer[1] = 0x00;
            packetBuffer[2] = 0x01;
            packetBuffer[3] = 0x01;
        }

        public void Open()
        {
            if (IsConnected) return;

            try
            {
                udpClient = new UdpClient();
                udpClient.DontFragment = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Nie udało się otworzyć gniazda UDP.", ex);
            }

            bool success = TrySetWledPowerState(true);
            if (!success)
            {
                Thread.Sleep(300);
                TrySetWledPowerState(true);
            }
        }

        public void Close()
        {
            if (!IsConnected) return;

            try
            {
                ClearLeds();
                Thread.Sleep(30);
                ClearLeds();

                TrySetWledPowerState(false);
            }
            finally
            {
                udpClient!.Close();
                udpClient.Dispose();
                udpClient = null;
            }
        }

        public void SendFrame(ReadOnlySpan<RgbColor> colors)
        {
            if (!IsConnected) return;

            int totalLedsToSend = Math.Min(colors.Length, currentLedCount);
            int ledsProcessed = 0;

            while (ledsProcessed < totalLedsToSend)
            {
                int ledsInThisChunk = Math.Min(MaxLedsPerPacket, totalLedsToSend - ledsProcessed);
                int dataLengthInBytes = ledsInThisChunk * 3;
                int byteOffset = ledsProcessed * 3;

                packetBuffer[4] = (byte)((byteOffset >> 24) & 0xFF);
                packetBuffer[5] = (byte)((byteOffset >> 16) & 0xFF);
                packetBuffer[6] = (byte)((byteOffset >> 8) & 0xFF);
                packetBuffer[7] = (byte)(byteOffset & 0xFF);

                packetBuffer[8] = (byte)((dataLengthInBytes >> 8) & 0xFF);
                packetBuffer[9] = (byte)(dataLengthInBytes & 0xFF);

                int bufferIndex = 10;
                for (int i = 0; i < ledsInThisChunk; i++)
                {
                    RgbColor c = colors[ledsProcessed + i];
                    packetBuffer[bufferIndex++] = c.R;
                    packetBuffer[bufferIndex++] = c.G;
                    packetBuffer[bufferIndex++] = c.B;
                }

                try
                {
                    udpClient!.Send(packetBuffer, 10 + dataLengthInBytes, endPoint);
                }
                catch (SocketException)
                {
                    // UDP: brak potwierdzenia dostawy jest normalny - ignorujemy pojedynczy zgubiony pakiet.
                }

                ledsProcessed += ledsInThisChunk;
            }
        }

        private void ClearLeds()
        {
            var blackFrame = new RgbColor[currentLedCount];
            SendFrame(blackFrame);
        }

        private bool TrySetWledPowerState(bool turnOn)
        {
            try
            {
                string json = turnOn
                    ? "{\"on\":true,\"transition\":1}"
                    : "{\"on\":false,\"transition\":1}";

                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = httpClient.PostAsync(httpBaseUrl, content).GetAwaiter().GetResult();

                bool isSuccess = response.IsSuccessStatusCode;

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: ustawiono zasilanie na {turnOn}, status HTTP: {(int)response.StatusCode}, sukces: {isSuccess}");

                return isSuccess;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: nie udało się ustawić zasilania na {turnOn} - {ex.Message}");
                return false;
            }
        }

        public async Task<List<string>> GetAvailableEffectsAsync()
        {
            var effects = new List<string>();

            try
            {
                using var response = await httpClient.GetAsync(effectsListUrl);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] WLED JSON API: nie udało się pobrać listy efektów, status HTTP: {(int)response.StatusCode}");
                    return effects;
                }

                string json = await response.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(json);

                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    effects.Add(item.GetString() ?? string.Empty);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: pobrano {effects.Count} efektów z urządzenia.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: błąd podczas pobierania listy efektów - {ex.Message}");
            }

            return effects;
        }

        public async Task<List<string>> GetAvailablePalettesAsync()
        {
            var palettes = new List<string>();

            try
            {
                using var response = await httpClient.GetAsync(palettesListUrl);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] WLED JSON API: nie udało się pobrać listy palet, status HTTP: {(int)response.StatusCode}");
                    return palettes;
                }

                string json = await response.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(json);

                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    palettes.Add(item.GetString() ?? string.Empty);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: pobrano {palettes.Count} palet z urządzenia.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: błąd podczas pobierania listy palet - {ex.Message}");
            }

            return palettes;
        }

        public async Task<bool> SetEffectAsync(
            int fxId,
            int speed = 128,
            int intensity = 128,
            int paletteId = 0,
            (byte R, byte G, byte B)? primaryColor = null,
            (byte R, byte G, byte B)? secondaryColor = null,
            int? brightness = null)
        {
            try
            {
                var color1 = primaryColor ?? (255, 255, 255);
                var color2 = secondaryColor ?? (0, 0, 0);

                var segPayload = new Dictionary<string, object>
                {
                    ["fx"] = fxId,
                    ["sx"] = speed,
                    ["ix"] = intensity,
                    ["pal"] = paletteId,
                    ["col"] = new[]
{
    new[] { (int)color1.R, (int)color1.G, (int)color1.B },
    new[] { (int)color2.R, (int)color2.G, (int)color2.B },
    new[] { 0, 0, 0 }
}
                };

                var rootPayload = new Dictionary<string, object>
                {
                    ["on"] = true,
                    ["seg"] = segPayload
                };

                if (brightness.HasValue)
                {
                    rootPayload["bri"] = brightness.Value;
                }

                string json = JsonSerializer.Serialize(rootPayload);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync(httpBaseUrl, content);

                bool isSuccess = response.IsSuccessStatusCode;

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: ustawiono efekt fx={fxId} pal={paletteId} bri={brightness}, status HTTP: {(int)response.StatusCode}, sukces: {isSuccess}");

                return isSuccess;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] WLED JSON API: nie udało się ustawić efektu fx={fxId} - {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            Close();
            httpClient.Dispose();
            isDisposed = true;
        }
    }
}