using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Hardware
{
    // Samodzielny klient podglądu na żywo (funkcja "Peek" z WLED) przez WebSocket.
    // Łączy się niezależnie od głównego silnika DDP - działa nawet gdy Ambilight nie jest
    // uruchomiony, bo korzysta wyłącznie z adresu IP urządzenia WLED.
    //
    // WAŻNE: odpowiedź na {"lv":true} to NIE jest JSON - to binarna ramka:
    //   byte[0]   = 'L' (0x4C)
    //   byte[1]   = wersja formatu (zawsze 1)
    //   byte[2..] = trójki RGB (3 bajty na diodę) aż do końca wiadomości
    // Liczba diod NIE jest przesyłana w nagłówku - wynika wyłącznie z długości
    // wiadomości: (długość - 2) / 3. Dla matrycy 2D WLED offset danych to 4 bajty,
    // ale dla zwykłego paska 1D (nasz przypadek) offset wynosi 2.
    // Źródło: WLED wled00/ws.cpp, funkcja sendLiveLedsWs()
    // (size_t pos = 2; // start of data - potwierdzone w kodzie źródłowym).
    public sealed class WledLivePreviewClient : IAsyncDisposable
    {
        private const int ReconnectDelayMs = 2000;
        private const byte LiveFrameMarker = (byte)'L';
        private const int StripHeaderLength = 2;

        private ClientWebSocket? webSocket;
        private CancellationTokenSource? lifecycleCts;
        private Task? receiveLoopTask;
        private string currentIpAddress = string.Empty;
        private volatile bool isStopped = true;
        private bool hasLoggedFirstFrame;

        public event Action<RgbColor[]>? LiveColorsReceived;
        public event Action<bool>? ConnectionStateChanged;

        public void Start(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                System.Diagnostics.Debug.WriteLine("[DIAG] WledLivePreview: pusty adres IP, pomijam start.");
                return;
            }

            if (!isStopped && string.Equals(currentIpAddress, ipAddress, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Stop();

            System.Diagnostics.Debug.WriteLine($"[DIAG] WledLivePreview: Start() wywołane dla IP={ipAddress}");

            currentIpAddress = ipAddress;
            isStopped = false;
            hasLoggedFirstFrame = false;
            lifecycleCts = new CancellationTokenSource();
            receiveLoopTask = Task.Run(() => ConnectionLoopAsync(lifecycleCts.Token));
        }

        public void Stop()
        {
            if (isStopped) return;
            isStopped = true;

            System.Diagnostics.Debug.WriteLine("[DIAG] WledLivePreview: Stop() wywołane.");

            try
            {
                lifecycleCts?.Cancel();
                webSocket?.Abort();
            }
            catch
            {
                // Best-effort - zamykamy połączenie, błędy przy zamykaniu ignorujemy.
            }
        }

        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    webSocket = new ClientWebSocket();
                    var uri = new Uri($"ws://{currentIpAddress}/ws");

                    System.Diagnostics.Debug.WriteLine($"[DIAG] WledLivePreview: łączenie z {uri}...");
                    await webSocket.ConnectAsync(uri, token);
                    System.Diagnostics.Debug.WriteLine("[DIAG] WledLivePreview: połączono, wysyłam żądanie lv:true.");

                    ConnectionStateChanged?.Invoke(true);

                    byte[] requestPeek = Encoding.UTF8.GetBytes("{\"lv\":true}");
                    await webSocket.SendAsync(requestPeek, WebSocketMessageType.Text, true, token);

                    await ReceiveLoopAsync(webSocket, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] WledLivePreview: błąd połączenia - {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    ConnectionStateChanged?.Invoke(false);
                    webSocket?.Dispose();
                    webSocket = null;
                }

                if (token.IsCancellationRequested) break;

                try
                {
                    await Task.Delay(ReconnectDelayMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            System.Diagnostics.Debug.WriteLine("[DIAG] WledLivePreview: ConnectionLoopAsync zakończony.");
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[16384];

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var messageStream = new System.IO.MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        System.Diagnostics.Debug.WriteLine("[DIAG] WledLivePreview: serwer zamknął połączenie.");
                        return;
                    }

                    messageStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] messageBytes = messageStream.ToArray();

                // Ramki Peek są binarne (Text=false przy WLED), ale bywa, że pierwsza wiadomość
                // po connect to zwykły JSON ze stanem urządzenia (state/info) - odróżniamy po
                // pierwszym bajcie: 'L' oznacza ramkę Peek, '{' oznacza zwykły JSON do zignorowania.
                if (messageBytes.Length == 0) continue;

                if (messageBytes[0] == LiveFrameMarker)
                {
                    RgbColor[]? colors = ParseLiveFrame(messageBytes);
                    if (colors != null)
                    {
                        if (!hasLoggedFirstFrame)
                        {
                            hasLoggedFirstFrame = true;
                            System.Diagnostics.Debug.WriteLine(
                                $"[DIAG] WledLivePreview: pierwsza ramka Peek odebrana, liczba diod={colors.Length}");
                        }

                        LiveColorsReceived?.Invoke(colors);
                    }
                }
                // Inne wiadomości (JSON state/info wysyłany przy connect) są celowo ignorowane -
                // interesuje nas wyłącznie strumień Peek.
            }
        }

        // Parsuje binarną ramkę Peek: 'L', wersja, potem od razu trójki RGB.
        // Nagłówek dla zwykłego paska 1D ma tylko 2 bajty - liczba diod wynika
        // wyłącznie z długości wiadomości, WLED nie wysyła jej w nagłówku.
        private static RgbColor[]? ParseLiveFrame(byte[] data)
        {
            if (data.Length < StripHeaderLength) return null;

            int count = (data.Length - StripHeaderLength) / 3;
            if (count <= 0) return null;

            var colors = new RgbColor[count];
            int offset = StripHeaderLength;

            for (int i = 0; i < count; i++)
            {
                byte r = data[offset++];
                byte g = data[offset++];
                byte b = data[offset++];
                colors[i] = new RgbColor(r, g, b);
            }

            return colors;
        }

        public async ValueTask DisposeAsync()
        {
            Stop();

            if (receiveLoopTask != null)
            {
                try
                {
                    await receiveLoopTask;
                }
                catch
                {
                    // Ignorujemy błędy przy końcowym oczekiwaniu na zamknięcie pętli.
                }
            }

            lifecycleCts?.Dispose();
        }
    }
}