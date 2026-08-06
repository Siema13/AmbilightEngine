using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Hardware
{
    // Punkt współdzielenia JEDNEGO fizycznego połączenia WebSocket Peek per adres IP WLED.
    // WLED serwuje strumień "lv":true tylko jednemu klientowi naraz - jeśli kilka kontrolek
    // podglądu (Dashboard, Ekran blokady, Bezczynność) otwiera własne, niezależne połączenia
    // do tego samego urządzenia, WLED zaczyna gubić/nie serwować ramek pozostałym klientom.
    // Skutek: część podglądów w ogóle nie aktualizuje się albo pokazuje stan "wygranego"
    // klienta, mimo że nie jest to ich własny kontekst.
    //
    // Hub utrzymuje jedno połączenie na adres IP, liczone referencyjnie (ref-counted) - gdy
    // pierwsza kontrolka się subskrybuje, startuje połączenie; gdy ostatnia się wypisuje,
    // połączenie jest zamykane. Odebrane ramki są rozsyłane do wszystkich aktywnych subskrybentów.
    public sealed class WledLivePreviewHub
    {
        public static WledLivePreviewHub Instance { get; } = new WledLivePreviewHub();

        private sealed class SharedConnection
        {
            public readonly WledLivePreviewClient Client = new WledLivePreviewClient();
            public readonly List<Subscription> Subscribers = new();
            public readonly object Lock = new();

            // NOWOŚĆ: śledzi aktualny stan połączenia, żeby nowy subskrybent (dołączający do
            // już aktywnego, współdzielonego połączenia) mógł natychmiast dostać prawdziwy stan,
            // a nie czekać na kolejną zmianę, która może już nigdy nie nadejść.
            public bool IsCurrentlyConnected;
        }

        private sealed class Subscription : IDisposable
        {
            private readonly WledLivePreviewHub owner;
            private readonly string ipAddress;
            private bool isDisposed;

            public Action<RgbColor[]>? OnColors;
            public Action<bool>? OnConnectionState;

            public Subscription(WledLivePreviewHub owner, string ipAddress)
            {
                this.owner = owner;
                this.ipAddress = ipAddress;
            }

            public void Dispose()
            {
                if (isDisposed) return;
                isDisposed = true;
                owner.Unsubscribe(ipAddress, this);
            }
        }

        private readonly ConcurrentDictionary<string, SharedConnection> connections =
            new(StringComparer.OrdinalIgnoreCase);

        private WledLivePreviewHub() { }

        // Rejestruje subskrybenta dla danego adresu IP. Zwrócony IDisposable MUSI zostać
        // zdysponowany (np. w Unloaded kontrolki albo przy zmianie IP), inaczej licznik
        // referencji nigdy nie spadnie do zera i połączenie zostanie otwarte na zawsze.
        public IDisposable Subscribe(string ipAddress, Action<RgbColor[]> onColors, Action<bool> onConnectionState)
        {
            SharedConnection connection = connections.GetOrAdd(ipAddress, ip =>
            {
                var created = new SharedConnection();
                created.Client.LiveColorsReceived += colors => BroadcastColors(created, colors);
                created.Client.ConnectionStateChanged += isConnected => BroadcastConnectionState(created, isConnected);
                return created;
            });

            var subscription = new Subscription(this, ipAddress)
            {
                OnColors = onColors,
                OnConnectionState = onConnectionState
            };

            lock (connection.Lock)
            {
                bool wasEmpty = connection.Subscribers.Count == 0;
                connection.Subscribers.Add(subscription);

                if (wasEmpty)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] WledLivePreviewHub: pierwszy subskrybent dla {ipAddress}, startuję współdzielone połączenie.");
                    connection.Client.Start(ipAddress);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] WledLivePreviewHub: kolejny subskrybent dla {ipAddress}, liczba subskrybentów={connection.Subscribers.Count}.");

                    // FIX: bez tego nowy subskrybent dołączający do już połączonego huba nigdy nie
                    // dostawał informacji o stanie - ConnectionStateChanged odpala się tylko przy
                    // ZMIANIE stanu, a tu żadna zmiana nie zaszła, więc UI zostawał na tekście
                    // domyślnym "łączenie..." mimo realnie odbieranych ramek Peek.
                    onConnectionState?.Invoke(connection.IsCurrentlyConnected);
                }
            }

            return subscription;
        }

        private void Unsubscribe(string ipAddress, Subscription subscription)
        {
            if (!connections.TryGetValue(ipAddress, out SharedConnection? connection)) return;

            lock (connection.Lock)
            {
                connection.Subscribers.Remove(subscription);

                if (connection.Subscribers.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] WledLivePreviewHub: ostatni subskrybent dla {ipAddress} wypisany, zamykam połączenie.");
                    connection.Client.Stop();
                    connections.TryRemove(ipAddress, out _);
                }
            }
        }

        private static void BroadcastColors(SharedConnection connection, RgbColor[] colors)
        {
            List<Subscription> snapshot;
            lock (connection.Lock)
            {
                snapshot = new List<Subscription>(connection.Subscribers);
            }

            foreach (Subscription sub in snapshot)
            {
                sub.OnColors?.Invoke(colors);
            }
        }

        private static void BroadcastConnectionState(SharedConnection connection, bool isConnected)
        {
            List<Subscription> snapshot;
            lock (connection.Lock)
            {
                connection.IsCurrentlyConnected = isConnected;
                snapshot = new List<Subscription>(connection.Subscribers);
            }

            foreach (Subscription sub in snapshot)
            {
                sub.OnConnectionState?.Invoke(isConnected);
            }
        }
    }
}