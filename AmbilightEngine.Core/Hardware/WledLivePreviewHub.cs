using System;
using System.Collections.Generic;
using AmbilightEngine.Core.Processing;

namespace AmbilightEngine.Core.Hardware
{
    // Centralny rejestr połączeń podglądu na żywo (WLED Peek), po jednym na adres IP.
    // KLUCZOWE: WLED obsługuje tylko jeden aktywny strumień "lv" naraz - jeśli kilka
    // niezależnych połączeń WebSocket wysyła {"lv":true}, każde kolejne "kradnie" strumień
    // poprzedniemu klientowi (dokumentacja WLED). Dlatego wszystkie kontrolki podglądu
    // (Dashboard, Ekran blokady, Bezczynność) muszą współdzielić TO SAMO połączenie,
    // zamiast każda otwierać własne.
    public static class WledLivePreviewHub
    {
        private sealed class HubEntry
        {
            public WledLivePreviewClient Client = new();
            public int SubscriberCount;
        }

        private static readonly Dictionary<string, HubEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object lockObj = new();

        // Subskrybuje podgląd dla danego adresu IP. Zwraca token, który MUSI zostać
        // przekazany do Unsubscribe przy zwalnianiu (np. w Unloaded kontrolki).
        public static IDisposable Subscribe(
            string ipAddress,
            Action<RgbColor[]> onColorsReceived,
            Action<bool> onConnectionStateChanged)
        {
            lock (lockObj)
            {
                if (!entries.TryGetValue(ipAddress, out HubEntry? entry))
                {
                    entry = new HubEntry();
                    entries[ipAddress] = entry;
                }

                entry.Client.LiveColorsReceived += onColorsReceived;
                entry.Client.ConnectionStateChanged += onConnectionStateChanged;
                entry.SubscriberCount++;

                if (entry.SubscriberCount == 1)
                {
                    entry.Client.Start(ipAddress);
                }

                return new Subscription(ipAddress, onColorsReceived, onConnectionStateChanged);
            }
        }

        private static void Unsubscribe(
            string ipAddress,
            Action<RgbColor[]> onColorsReceived,
            Action<bool> onConnectionStateChanged)
        {
            lock (lockObj)
            {
                if (!entries.TryGetValue(ipAddress, out HubEntry? entry)) return;

                entry.Client.LiveColorsReceived -= onColorsReceived;
                entry.Client.ConnectionStateChanged -= onConnectionStateChanged;
                entry.SubscriberCount--;

                if (entry.SubscriberCount <= 0)
                {
                    entry.Client.Stop();
                    entries.Remove(ipAddress);
                }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly string ipAddress;
            private readonly Action<RgbColor[]> onColorsReceived;
            private readonly Action<bool> onConnectionStateChanged;
            private bool isDisposed;

            public Subscription(string ipAddress, Action<RgbColor[]> onColorsReceived, Action<bool> onConnectionStateChanged)
            {
                this.ipAddress = ipAddress;
                this.onColorsReceived = onColorsReceived;
                this.onConnectionStateChanged = onConnectionStateChanged;
            }

            public void Dispose()
            {
                if (isDisposed) return;
                isDisposed = true;
                Unsubscribe(ipAddress, onColorsReceived, onConnectionStateChanged);
            }
        }
    }
}