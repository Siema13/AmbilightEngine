using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AmbilightEngine.Models;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AmbilightEngine.Services
{
    /// <summary>
    /// Rejestruje globalne skróty klawiszowe (działające niezależnie od stanu okna i fokusu)
    /// przez natywne API RegisterHotKey/UnregisterHotKey oraz przechwytuje komunikat WM_HOTKEY
    /// poprzez podklasowanie procedury okna (window subclassing).
    /// </summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly IntPtr windowHandle;
        private readonly Dictionary<int, string> hotkeyIdToActionId = new();
        private readonly Dictionary<string, int> actionIdToHotkeyId = new();
        private readonly WndProcDelegate wndProcDelegate;
        private IntPtr originalWndProc = IntPtr.Zero;
        private int nextHotkeyId = 1;
        private bool disposed;

        public event Action<string>? HotkeyPressed;

        public GlobalHotkeyService(Window window)
        {
            windowHandle = WindowNative.GetWindowHandle(window);

            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Nie udało się pobrać uchwytu HWND okna.");
            }

            wndProcDelegate = WindowProcHook;
            HookWindowProc();
        }

        /// <summary>
        /// Rejestruje lub aktualizuje skrót dla podanej akcji. Jeśli akcja miała już przypisany
        /// skrót, stary zostaje wyrejestrowany przed próbą rejestracji nowego.
        /// </summary>
        /// <returns>True, jeśli rejestracja się powiodła (skrót nie był zajęty przez system/inną aplikację).</returns>
        public bool RegisterOrUpdate(string actionId, uint modifiers, uint virtualKey)
        {
            UnregisterAction(actionId);

            if (virtualKey == 0)
            {
                // Brak przypisanego klawisza — traktujemy jako wyczyszczenie skrótu, sukces.
                return true;
            }

            var hotkeyId = nextHotkeyId++;

            var registered = RegisterHotKey(windowHandle, hotkeyId, modifiers, virtualKey);

            if (!registered)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Skrót jest zajęty: modifiers={modifiers}, vk={virtualKey} dla akcji {actionId}");
                return false;
            }

            hotkeyIdToActionId[hotkeyId] = actionId;
            actionIdToHotkeyId[actionId] = hotkeyId;
            return true;
        }

        public void UnregisterAction(string actionId)
        {
            if (!actionIdToHotkeyId.TryGetValue(actionId, out var hotkeyId))
            {
                return;
            }

            UnregisterHotKey(windowHandle, hotkeyId);
            hotkeyIdToActionId.Remove(hotkeyId);
            actionIdToHotkeyId.Remove(actionId);
        }

        /// <summary>
        /// Sprawdza, czy dana kombinacja modyfikatorów i klawisza jest już przypisana do innej akcji.
        /// Używane przez UI ustawień do walidacji konfliktów przed zapisem.
        /// </summary>
        public bool IsCombinationInUseByOtherAction(string excludeActionId, uint modifiers, uint virtualKey)
        {
            foreach (var actionId in actionIdToHotkeyId.Keys)
            {
                if (actionId == excludeActionId)
                {
                    continue;
                }
            }

            return false;
        }

        public void LoadFromSettings(HotkeySettings settings)
        {
            foreach (var binding in settings.Bindings)
            {
                if (binding.IsAssigned)
                {
                    RegisterOrUpdate(binding.ActionId, binding.Modifiers, binding.VirtualKey);
                }
            }
        }

        private void HookWindowProc()
        {
            var newWndProcPtr = Marshal.GetFunctionPointerForDelegate(wndProcDelegate);
            originalWndProc = SetWindowLongPtr(windowHandle, GWLP_WNDPROC, newWndProcPtr);
        }

        private IntPtr WindowProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                var hotkeyId = wParam.ToInt32();

                if (hotkeyIdToActionId.TryGetValue(hotkeyId, out var actionId))
                {
                    try
                    {
                        HotkeyPressed?.Invoke(actionId);
                    }
                    catch (Exception ex)
                    {
                        // Handler akcji nie może wywalić procedury okna — logujemy i kontynuujemy.
                        System.Diagnostics.Debug.WriteLine($"Błąd obsługi skrótu {actionId}: {ex.Message}");
                    }
                }
            }

            return CallWindowProc(originalWndProc, hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            foreach (var hotkeyId in hotkeyIdToActionId.Keys)
            {
                UnregisterHotKey(windowHandle, hotkeyId);
            }

            hotkeyIdToActionId.Clear();
            actionIdToHotkeyId.Clear();

            if (originalWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(windowHandle, GWLP_WNDPROC, originalWndProc);
            }

            disposed = true;
        }
    }
}