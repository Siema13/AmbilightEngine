using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Hardware
{
    public sealed class WledDdpNetworkSender : IOutputDevice
    {
        // Zabezpieczenie przed fragmentacją pakietu w sieci Wi-Fi (limit MTU).
        // 480 diod * 3 bajty = 1440 bajtów danych + 10 bajtów nagłówka DDP = 1450 bajtów. Bezpieczny margines.
        private const int MaxLedsPerPacket = 480;

        private readonly IPEndPoint endPoint;
        private readonly string httpBaseUrl;
        private readonly HttpClient httpClient;

        private UdpClient? udpClient;
        private byte[] packetBuffer = Array.Empty<byte>();
        private int currentLedCount;
        private bool isDisposed;

        public bool IsConnected => udpClient != null;

        public WledDdpNetworkSender(string ipAddress, int initialLedCount)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress, out IPAddress? ip))
                throw new ArgumentException("Nieprawidłowy adres IP ESP32/WLED.");

            endPoint = new IPEndPoint(ip, 4048); // Standardowy port DDP używany przez WLED
            httpBaseUrl = $"http://{ipAddress}/json/state";

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

            // Alokujemy bufor o maksymalnym możliwym rozmiarze raz, żeby uniknąć realokacji w locie
            int maxBufferSize = 10 + (MaxLedsPerPacket * 3);
            packetBuffer = new byte[maxBufferSize];

            packetBuffer[0] = 0x41; // Flagi: wersja 1, PUSH
            packetBuffer[1] = 0x00; // Sekwencja
            packetBuffer[2] = 0x01; // Typ danych: RGB
            packetBuffer[3] = 0x01; // Destination ID
        }

        public void Open()
        {
            if (IsConnected) return;

            try
            {
                udpClient = new UdpClient();
                udpClient.DontFragment = true; // Wymuszamy, by router nie dzielił naszych pakietów
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Nie udało się otworzyć gniazda UDP.", ex);
            }

            // Przy każdym starcie jawnie włączamy urządzenie przez API, z jedną próbą retry,
            // na wypadek gdyby poprzednia komenda "off" z Close() nadal była w locie.
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
                    // UDP: brak potwierdzenia dostawy jest normalny (Fire-and-Forget) - ignorujemy pojedynczy zgubiony pakiet.
                }

                ledsProcessed += ledsInThisChunk;
            }
        }

        private void ClearLeds()
        {
            var blackFrame = new RgbColor[currentLedCount];
            SendFrame(blackFrame);
        }

        // Wysyła synchroniczną (z krótkim timeoutem) komendę HTTP JSON API do WLED,
        // aby jawnie ustawić stan zasilania. Jawnie wymuszamy "transition":1 (nie 0!) -
        // znana usterka firmware WLED (GitHub Aircoookie/WLED #3720) powoduje, że przy
        // transition:0 komendy "on" i "bri" są całkowicie ignorowane przez silnik renderujący,
        // mimo że API wciąż zwraca kod sukcesu HTTP 200. Zwraca true, jeśli WLED odpowiedziało kodem sukcesu.
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

        public void Dispose()
        {
            if (isDisposed) return;
            Close();
            httpClient.Dispose();
            isDisposed = true;
        }
    }
}