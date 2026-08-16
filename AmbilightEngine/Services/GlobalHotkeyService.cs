using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AmbilightEngine.Models;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AmbilightEngine.Services
{
    /// <summary>
    /// Rejestruje skróty globalne Win32 przez RegisterHotKey oraz odbiera WM_HOTKEY
    /// bezpiecznie przez SetWindowSubclass. Może współistnieć na tym samym HWND
    /// z WtsSessionMessageMonitor, ponieważ każdy subclass ma własny identyfikator.
    /// </summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const uint WmHotkey = 0x0312;

        // "HKEY" — identyfikator inny niż "AMBI" użyty przez WtsSessionMessageMonitor.
        private static readonly IntPtr SubclassId = new IntPtr(0x484B4559);

        private delegate IntPtr SubclassProcDelegate(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd,
            SubclassProcDelegate pfnSubclass,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd,
            SubclassProcDelegate pfnSubclass,
            IntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(
            IntPtr hWnd,
            int id);

        private sealed class RegisteredHotkey
        {
            public int HotkeyId { get; init; }

            public uint Modifiers { get; init; }

            public uint VirtualKey { get; init; }
        }

        private readonly IntPtr windowHandle;
        private readonly Dictionary<int, string> hotkeyIdToActionId = new();
        private readonly Dictionary<string, RegisteredHotkey> actionIdToHotkey = new();
        private readonly SubclassProcDelegate subclassProcDelegate;

        private int nextHotkeyId = 1;
        private bool isSubclassed;
        private bool disposed;

        /// <summary>
        /// Zgłaszane po odebraniu WM_HOTKEY. Parametr to ActionId ze słownika HotkeyActionIds.
        /// </summary>
        public event Action<string>? HotkeyPressed;

        public GlobalHotkeyService(Window window)
        {
            windowHandle = WindowNative.GetWindowHandle(window);

            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Nie udało się pobrać uchwytu HWND głównego okna.");
            }

            subclassProcDelegate = SubclassWndProc;

            isSubclassed = SetWindowSubclass(
                windowHandle,
                subclassProcDelegate,
                SubclassId,
                IntPtr.Zero);

            if (!isSubclassed)
            {
                int error = Marshal.GetLastWin32Error();

                throw new InvalidOperationException(
                    $"Nie udało się podłączyć obsługi skrótów globalnych. Win32 error: {error}.");
            }

            System.Diagnostics.Debug.WriteLine(
                "[DIAG] GlobalHotkeyService: SetWindowSubclass zakończone powodzeniem.");
        }

        /// <summary>
        /// Rejestruje albo aktualizuje skrót akcji. virtualKey == 0 oznacza usunięcie
        /// przypisania; w takiej sytuacji stary skrót jest zawsze wyrejestrowywany.
        /// </summary>
        public bool RegisterOrUpdate(
            string actionId,
            uint modifiers,
            uint virtualKey)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GlobalHotkeyService));
            }

            if (string.IsNullOrWhiteSpace(actionId))
            {
                throw new ArgumentException(
                    "Identyfikator akcji nie może być pusty.",
                    nameof(actionId));
            }

            // Najpierw wyrejestrowujemy wcześniejszy skrót tej samej akcji.
            UnregisterAction(actionId);

            if (virtualKey == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] GlobalHotkeyService: wyczyszczono skrót akcji '{actionId}'.");

                return true;
            }

            int hotkeyId = nextHotkeyId++;

            bool registered = RegisterHotKey(
                windowHandle,
                hotkeyId,
                modifiers,
                virtualKey);

            if (!registered)
            {
                int error = Marshal.GetLastWin32Error();

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] GlobalHotkeyService: RegisterHotKey nieudane. " +
                    $"Akcja='{actionId}', modifiers={modifiers}, vk={virtualKey}, error={error}.");

                return false;
            }

            hotkeyIdToActionId[hotkeyId] = actionId;

            actionIdToHotkey[actionId] = new RegisteredHotkey
            {
                HotkeyId = hotkeyId,
                Modifiers = modifiers,
                VirtualKey = virtualKey
            };

            System.Diagnostics.Debug.WriteLine(
                $"[DIAG] GlobalHotkeyService: zarejestrowano '{actionId}', " +
                $"id={hotkeyId}, modifiers={modifiers}, vk={virtualKey}.");

            return true;
        }

        /// <summary>
        /// Usuwa systemową rejestrację skrótu oraz wpisy z lokalnych słowników.
        /// Wywołanie dla akcji bez przypisania jest bezpieczne.
        /// </summary>
        public void UnregisterAction(string actionId)
        {
            if (!actionIdToHotkey.TryGetValue(actionId, out RegisteredHotkey? existing))
            {
                return;
            }

            bool unregistered = UnregisterHotKey(
                windowHandle,
                existing.HotkeyId);

            if (!unregistered)
            {
                int error = Marshal.GetLastWin32Error();

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] GlobalHotkeyService: UnregisterHotKey zwróciło false. " +
                    $"Akcja='{actionId}', id={existing.HotkeyId}, error={error}.");
            }

            hotkeyIdToActionId.Remove(existing.HotkeyId);
            actionIdToHotkey.Remove(actionId);

            System.Diagnostics.Debug.WriteLine(
                $"[DIAG] GlobalHotkeyService: wyrejestrowano '{actionId}'.");
        }

        /// <summary>
        /// Zwraca true, gdy dokładnie taka kombinacja jest przypisana do innej akcji
        /// wewnątrz aplikacji. Ta metoda jest używana przez UI jeszcze przed wywołaniem
        /// RegisterHotKey, dzięki czemu można wskazać użytkownikowi nazwę konfliktującej akcji.
        /// </summary>
        public bool IsCombinationInUseByOtherAction(
            string excludeActionId,
            uint modifiers,
            uint virtualKey)
        {
            if (virtualKey == 0)
            {
                return false;
            }

            foreach (KeyValuePair<string, RegisteredHotkey> entry in actionIdToHotkey)
            {
                if (string.Equals(
                        entry.Key,
                        excludeActionId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                RegisteredHotkey hotkey = entry.Value;

                if (hotkey.Modifiers == modifiers &&
                    hotkey.VirtualKey == virtualKey)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Zwraca ActionId, które używa podanej kombinacji, lub null gdy kombinacja
        /// nie jest zarejestrowana przez tę aplikację.
        /// </summary>
        public string? FindActionIdByCombination(
            uint modifiers,
            uint virtualKey)
        {
            if (virtualKey == 0)
            {
                return null;
            }

            foreach (KeyValuePair<string, RegisteredHotkey> entry in actionIdToHotkey)
            {
                RegisteredHotkey hotkey = entry.Value;

                if (hotkey.Modifiers == modifiers &&
                    hotkey.VirtualKey == virtualKey)
                {
                    return entry.Key;
                }
            }

            return null;
        }

        /// <summary>
        /// Rejestruje przypisania zapisane w ustawieniach. Błędy pojedynczych wpisów
        /// nie przerywają wczytywania pozostałych skrótów.
        /// </summary>
        public void LoadFromSettings(HotkeySettings settings)
        {
            if (settings?.Bindings is null)
            {
                return;
            }

            foreach (HotkeyBinding binding in settings.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.ActionId) ||
                    !binding.IsAssigned)
                {
                    continue;
                }

                bool registered = RegisterOrUpdate(
                    binding.ActionId,
                    binding.Modifiers,
                    binding.VirtualKey);

                if (!registered)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] GlobalHotkeyService: nie wczytano skrótu " +
                        $"'{binding.ToDisplayString()}' dla '{binding.ActionId}'.");
                }
            }
        }

        private IntPtr SubclassWndProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            IntPtr uIdSubclass,
            IntPtr dwRefData)
        {
            if (uMsg == WmHotkey)
            {
                int hotkeyId = wParam.ToInt32();

                if (hotkeyIdToActionId.TryGetValue(
                        hotkeyId,
                        out string? actionId))
                {
                    try
                    {
                        HotkeyPressed?.Invoke(actionId);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[DIAG] Błąd obsługi skrótu '{actionId}': {ex}");
                    }
                }
            }

            return DefSubclassProc(
                hWnd,
                uMsg,
                wParam,
                lParam);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            // Tworzymy kopię, ponieważ UnregisterAction usuwa elementy ze słownika.
            string[] actionIds = new string[actionIdToHotkey.Count];
            actionIdToHotkey.Keys.CopyTo(actionIds, 0);

            foreach (string actionId in actionIds)
            {
                UnregisterAction(actionId);
            }

            hotkeyIdToActionId.Clear();
            actionIdToHotkey.Clear();

            if (isSubclassed)
            {
                bool removed = RemoveWindowSubclass(
                    windowHandle,
                    subclassProcDelegate,
                    SubclassId);

                if (!removed)
                {
                    int error = Marshal.GetLastWin32Error();

                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] GlobalHotkeyService: RemoveWindowSubclass zwróciło false, error={error}.");
                }

                isSubclassed = false;
            }

            disposed = true;
        }
    }
}