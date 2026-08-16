using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AmbilightEngine.Models;
using AmbilightEngine.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmbilightEngine.Pages
{
    public sealed partial class HotkeysSettingsPage : Page
    {
        private const int WhKeyboardLl = 13;

        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;

        private const uint VkEscape = 0x1B;
        private const uint VkBackspace = 0x08;
        private const uint VkDelete = 0x2E;

        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkLeftWindows = 0x5B;
        private const int VkRightWindows = 0x5C;

        private const int ModifierStateDownMask = unchecked((int)0x8000);

        private static readonly HashSet<uint> ModifierOnlyVirtualKeys = new()
        {
            0x10,
            0x11,
            0x12,
            0xA0,
            0xA1,
            0xA2,
            0xA3,
            0xA4,
            0xA5,
            0x5B,
            0x5C
        };

        private delegate IntPtr LowLevelKeyboardProc(
            int nCode,
            IntPtr wParam,
            IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr DwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            LowLevelKeyboardProc lpfn,
            IntPtr hmod,
            uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(
            IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk,
            int nCode,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(
            int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(
            string? lpModuleName);

        private MainWindow? mainWindow;
        private Button? activeCaptureButton;
        private string? activeCaptureActionId;

        private readonly LowLevelKeyboardProc keyboardHookDelegate;
        private readonly DispatcherQueue dispatcherQueue;

        private IntPtr keyboardHookHandle = IntPtr.Zero;
        private bool isCaptureKeyBeingHandled;

        public HotkeysSettingsPage()
        {
            InitializeComponent();

            keyboardHookDelegate = KeyboardHookCallback;
            dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            Loaded += HotkeysSettingsPage_Loaded;
            Unloaded += HotkeysSettingsPage_Unloaded;
        }

        private void HotkeysSettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;

            if (mainWindow is null)
            {
                return;
            }

            RefreshAllButtonLabels();
            RefreshButtonLabel(HotkeyActionIds.BrightnessUp);
            RefreshButtonLabel(HotkeyActionIds.BrightnessDown);
        }

        private void HotkeysSettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopKeyboardCapture();

            activeCaptureButton = null;
            activeCaptureActionId = null;
        }

        private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not string actionId)
            {
                return;
            }

            if (mainWindow is null)
            {
                return;
            }

            if (activeCaptureButton is not null &&
                activeCaptureButton != button)
            {
                CancelActiveCapture();
            }

            activeCaptureButton = button;
            activeCaptureActionId = actionId;

            button.Content = "Naciśnij kombinację...";
            ConflictInfoBar.IsOpen = false;

            if (!StartKeyboardCapture())
            {
                ShowConflict(
                    "Nie udało się uruchomić przechwytywania klawiatury. " +
                    "Spróbuj ponownie albo uruchom aplikację jako administrator.");

                CancelActiveCapture();
            }
        }

        private bool StartKeyboardCapture()
        {
            if (keyboardHookHandle != IntPtr.Zero)
            {
                return true;
            }

            IntPtr moduleHandle = GetModuleHandle(null);

            keyboardHookHandle = SetWindowsHookEx(
                WhKeyboardLl,
                keyboardHookDelegate,
                moduleHandle,
                0);

            if (keyboardHookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();

                Debug.WriteLine(
                    $"[DIAG] Hotkey capture: SetWindowsHookEx nieudane, error={error}.");

                return false;
            }

            Debug.WriteLine(
                "[DIAG] Hotkey capture: WH_KEYBOARD_LL uruchomiony.");

            return true;
        }

        private void StopKeyboardCapture()
        {
            if (keyboardHookHandle == IntPtr.Zero)
            {
                return;
            }

            bool result = UnhookWindowsHookEx(keyboardHookHandle);

            if (!result)
            {
                int error = Marshal.GetLastWin32Error();

                Debug.WriteLine(
                    $"[DIAG] Hotkey capture: UnhookWindowsHookEx nieudane, error={error}.");
            }

            keyboardHookHandle = IntPtr.Zero;
        }

        private IntPtr KeyboardHookCallback(
            int nCode,
            IntPtr wParam,
            IntPtr lParam)
        {
            if (nCode >= 0 &&
                (wParam.ToInt32() == WmKeyDown ||
                 wParam.ToInt32() == WmSysKeyDown) &&
                activeCaptureButton is not null &&
                activeCaptureActionId is not null &&
                !isCaptureKeyBeingHandled)
            {
                KbdLlHookStruct keyData =
                    Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

                uint virtualKey = keyData.VkCode;

                if (!ModifierOnlyVirtualKeys.Contains(virtualKey))
                {
                    uint modifiers = GetCurrentModifierFlags();

                    Debug.WriteLine(
                        $"[DIAG] Hotkey capture: action={activeCaptureActionId}, " +
                        $"vk={virtualKey}, modifiers={modifiers}.");

                    isCaptureKeyBeingHandled = true;

                    bool queued = dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            HandleCapturedVirtualKey(
                                virtualKey,
                                modifiers);
                        }
                        finally
                        {
                            isCaptureKeyBeingHandled = false;
                        }
                    });

                    if (queued)
                    {
                        // Blokujemy dotarcie klawisza do Button/ListView/Frame,
                        // aby Ctrl+cyfra nie uruchamiała skrótu/nawigacji WinUI.
                        return new IntPtr(1);
                    }

                    isCaptureKeyBeingHandled = false;
                }
            }

            return CallNextHookEx(
                keyboardHookHandle,
                nCode,
                wParam,
                lParam);
        }

        private void HandleCapturedVirtualKey(
            uint virtualKey,
            uint modifiers)
        {
            if (activeCaptureButton is null ||
                activeCaptureActionId is null ||
                mainWindow is null)
            {
                return;
            }

            if (virtualKey == VkEscape)
            {
                CancelActiveCapture();
                return;
            }

            if (virtualKey == VkBackspace ||
                virtualKey == VkDelete)
            {
                ClearHotkey(activeCaptureActionId);
                CancelActiveCapture();
                return;
            }

            if (modifiers == 0)
            {
                activeCaptureButton.Content =
                    "Dodaj Ctrl, Alt, Shift lub Win";

                return;
            }

            TryAssignHotkey(
                activeCaptureActionId,
                modifiers,
                virtualKey);
        }

        private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button ||
                button.Tag is not string actionId)
            {
                return;
            }

            ClearHotkey(actionId);
            CancelActiveCapture();
        }

        private void TryAssignHotkey(
            string actionId,
            uint modifiers,
            uint virtualKey)
        {
            if (mainWindow is null)
            {
                return;
            }

            string? conflictingActionId = FindConflictInSettings(
                actionId,
                modifiers,
                virtualKey);

            if (conflictingActionId is not null)
            {
                ShowConflict(
                    $"Kombinacja {FormatCombination(modifiers, virtualKey)} jest już przypisana do akcji: " +
                    $"{GetActionDisplayName(conflictingActionId)}.");

                return;
            }

            GlobalHotkeyService? hotkeyService =
                mainWindow.GlobalHotkeyService;

            if (hotkeyService is null)
            {
                ShowConflict(
                    "Usługa skrótów globalnych nie została uruchomiona. " +
                    "Zrestartuj aplikację i spróbuj ponownie.");

                return;
            }

            HotkeyBinding binding = GetOrCreateBinding(actionId);

            uint previousModifiers = binding.Modifiers;
            uint previousVirtualKey = binding.VirtualKey;

            bool registered = hotkeyService.RegisterOrUpdate(
                actionId,
                modifiers,
                virtualKey);

            if (!registered)
            {
                if (previousVirtualKey != 0)
                {
                    hotkeyService.RegisterOrUpdate(
                        actionId,
                        previousModifiers,
                        previousVirtualKey);
                }

                ShowConflict(
                    $"Kombinacja {FormatCombination(modifiers, virtualKey)} nie może zostać zarejestrowana. " +
                    "Jest zajęta przez Windows lub inną aplikację.");

                return;
            }

            binding.Modifiers = modifiers;
            binding.VirtualKey = virtualKey;

            mainWindow.SettingsService.Save(mainWindow.Settings);

            RefreshButtonLabel(actionId);

            activeCaptureButton = null;
            activeCaptureActionId = null;

            StopKeyboardCapture();

            ConflictInfoBar.IsOpen = false;
        }

        private void ClearHotkey(string actionId)
        {
            if (mainWindow is null)
            {
                return;
            }

            GlobalHotkeyService? hotkeyService =
                mainWindow.GlobalHotkeyService;

            if (hotkeyService is null)
            {
                ShowConflict(
                    "Usługa skrótów globalnych nie została uruchomiona. " +
                    "Nie można wyrejestrować skrótu.");

                return;
            }

            bool removed = hotkeyService.RegisterOrUpdate(
                actionId,
                modifiers: 0,
                virtualKey: 0);

            if (!removed)
            {
                ShowConflict(
                    "Nie udało się wyrejestrować skrótu globalnego.");

                return;
            }

            HotkeyBinding binding = GetOrCreateBinding(actionId);
            binding.Modifiers = 0;
            binding.VirtualKey = 0;

            mainWindow.SettingsService.Save(mainWindow.Settings);

            RefreshButtonLabel(actionId);

            ConflictInfoBar.IsOpen = false;
        }

        private string? FindConflictInSettings(
            string actionId,
            uint modifiers,
            uint virtualKey)
        {
            if (mainWindow is null || virtualKey == 0)
            {
                return null;
            }

            foreach (HotkeyBinding binding in
                     mainWindow.Settings.Hotkeys.Bindings)
            {
                if (string.Equals(
                        binding.ActionId,
                        actionId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (binding.IsAssigned &&
                    binding.Modifiers == modifiers &&
                    binding.VirtualKey == virtualKey)
                {
                    return binding.ActionId;
                }
            }

            return null;
        }

        private void ShowConflict(string message)
        {
            ConflictInfoBar.Message = message;
            ConflictInfoBar.IsOpen = true;

            Debug.WriteLine(
                $"[DIAG] Konflikt skrótu: {message}");

            if (activeCaptureButton is not null)
            {
                activeCaptureButton.Content =
                    "Konflikt — wybierz inną kombinację";
            }
        }

        private HotkeyBinding GetOrCreateBinding(string actionId)
        {
            if (mainWindow is null)
            {
                throw new InvalidOperationException(
                    "Nie można odczytać konfiguracji skrótów bez MainWindow.");
            }

            HotkeyBinding? binding =
                mainWindow.Settings.Hotkeys.Bindings.Find(
                    item => string.Equals(
                        item.ActionId,
                        actionId,
                        StringComparison.Ordinal));

            if (binding is not null)
            {
                return binding;
            }

            binding = new HotkeyBinding(actionId, 0, 0);
            mainWindow.Settings.Hotkeys.Bindings.Add(binding);

            return binding;
        }

        private void CancelActiveCapture()
        {
            if (activeCaptureActionId is not null)
            {
                RefreshButtonLabel(activeCaptureActionId);
            }

            activeCaptureButton = null;
            activeCaptureActionId = null;

            StopKeyboardCapture();
        }

        private void RefreshAllButtonLabels()
        {
            RefreshButtonLabel(HotkeyActionIds.ToggleEngine);
            RefreshButtonLabel(HotkeyActionIds.CycleMode);
            RefreshButtonLabel(HotkeyActionIds.Blackout);
            RefreshButtonLabel(HotkeyActionIds.CycleWhitePreset);
        }

        private void RefreshButtonLabel(string actionId)
        {
            if (mainWindow is null)
            {
                return;
            }

            Button? button = FindButtonForAction(actionId);

            if (button is null)
            {
                return;
            }

            HotkeyBinding? binding =
                mainWindow.Settings.Hotkeys.Bindings.Find(
                    item => string.Equals(
                        item.ActionId,
                        actionId,
                        StringComparison.Ordinal));

            button.Content = binding?.ToDisplayString() ?? "Brak";
        }

        private Button? FindButtonForAction(string actionId)
        {
            return actionId switch
            {
                HotkeyActionIds.ToggleEngine => ToggleEngineHotkeyButton,
                HotkeyActionIds.CycleMode => CycleModeHotkeyButton,
                HotkeyActionIds.Blackout => BlackoutHotkeyButton,
                HotkeyActionIds.CycleWhitePreset =>
                    CycleWhitePresetHotkeyButton,
                HotkeyActionIds.BrightnessUp => BrightnessUpHotkeyButton,
                HotkeyActionIds.BrightnessDown => BrightnessDownHotkeyButton,
                _ => null
            };
        }

        private static uint GetCurrentModifierFlags()
        {
            uint modifiers = 0;

            if (IsVirtualKeyDown(VkControl))
            {
                modifiers |= HotkeyBinding.MOD_CONTROL;
            }

            if (IsVirtualKeyDown(VkMenu))
            {
                modifiers |= HotkeyBinding.MOD_ALT;
            }

            if (IsVirtualKeyDown(VkShift))
            {
                modifiers |= HotkeyBinding.MOD_SHIFT;
            }

            if (IsVirtualKeyDown(VkLeftWindows) ||
                IsVirtualKeyDown(VkRightWindows))
            {
                modifiers |= HotkeyBinding.MOD_WIN;
            }

            return modifiers;
        }

        private static bool IsVirtualKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) &
                    ModifierStateDownMask) != 0;
        }

        private static string FormatCombination(
            uint modifiers,
            uint virtualKey)
        {
            return new HotkeyBinding(
                actionId: string.Empty,
                modifiers: modifiers,
                virtualKey: virtualKey).ToDisplayString();
        }

        private static string GetActionDisplayName(string? actionId)
        {
            return actionId switch
            {
                HotkeyActionIds.ToggleEngine =>
                    "Włącz / wyłącz Video Sync",

                HotkeyActionIds.CycleMode =>
                    "Przełącz tryb wyświetlania",

                HotkeyActionIds.Blackout =>
                    "Blackout",

                HotkeyActionIds.CycleWhitePreset =>
                    "Temperatura światła białego",
                HotkeyActionIds.BrightnessUp => "Zwiększ jasność o 5%",
                HotkeyActionIds.BrightnessDown => "Zmniejsz jasność o 5%",
                _ => "nieznana akcja"
            };
        }
    }
}