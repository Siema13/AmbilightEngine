using System;
using System.Threading;
using Microsoft.Win32;

namespace AmbilightEngine.Core.SystemState
{
    public enum SystemAmbientTrigger
    {
        None,
        LockOrSleep,
        Idle
    }

    // Nasłuchuje zdarzeń systemowych Windows (blokada, uśpienie, bezczynność)
    // i zgłasza zdarzenie, gdy aplikacja powinna przełączyć się w tryb ambientowy lub wrócić do normalnej pracy.
    // Wykrywanie blokady ekranu jest teraz w pełni zdarzeniowe (WM_WTSSESSION_CHANGE przez
    // WtsSessionMessageMonitor) - poprzednie podejścia oparte na pollingu (OpenInputDesktop,
    // WTSQuerySessionInformation) okazały się niewiarygodne w testach na tym środowisku.
    public sealed class SystemStateWatcher : IDisposable
    {
        // TYMCZASOWY LOG DIAGNOSTYCZNY - zapisuje do pliku na Pulpicie, niezależnie od
        // stanu okna Visual Studio. USUNĄĆ po ostatecznym potwierdzeniu stabilności.
        private static readonly string DiagLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "ambilight_diag.log");

        // AmbilightEngine.Core/SystemState/SystemStateWatcher.cs
        private static void WriteDiagLog(string message)
        {
            if (!DiagnosticsConfig.IsFileLoggingEnabled) return;

            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}\n";
                System.IO.File.AppendAllText(DiagLogPath, line);
            }
            catch
            {
                // Log diagnostyczny nie może wywalić aplikacji.
            }
        }

        private readonly AmbilightSettings settings;
        private readonly Timer idleCheckTimer;
        private readonly WtsSessionMessageMonitor? messageMonitor;
        private bool isLockedOrAsleep;
        private bool isIdleTriggered;

        public event Action<SystemAmbientTrigger>? AmbientModeRequested;
        public event Action? NormalModeRequested;

        // hwnd MUSI być realnym uchwytem głównego okna aplikacji (WindowNative.GetWindowHandle) -
        // jest niezbędny do podczepienia się pod komunikat WM_WTSSESSION_CHANGE.
        public SystemStateWatcher(AmbilightSettings settings, IntPtr windowHandle)
        {
            WriteDiagLog("SystemStateWatcher: konstruktor wywołany, rejestruję zdarzenia.");
            this.settings = settings;

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (windowHandle != IntPtr.Zero)
            {
                messageMonitor = new WtsSessionMessageMonitor(windowHandle);
                messageMonitor.SessionLocked += () =>
                {
                    WriteDiagLog("WtsSessionMessageMonitor: SessionLocked (WM_WTSSESSION_CHANGE).");
                    TriggerLockMode();
                };
                messageMonitor.SessionUnlocked += () =>
                {
                    WriteDiagLog("WtsSessionMessageMonitor: SessionUnlocked (WM_WTSSESSION_CHANGE).");
                    TriggerUnlockMode();
                };
            }
            else
            {
                WriteDiagLog("SystemStateWatcher: windowHandle == IntPtr.Zero, WtsSessionMessageMonitor NIE zainicjalizowany!");
            }

            // Timer obsługuje TYLKO wykrywanie bezczynności - wykrywanie blokady ekranu
            // jest teraz w pełni zdarzeniowe i nie wymaga pollingu.
            idleCheckTimer = new Timer(CheckIdleState, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            WriteDiagLog("SystemStateWatcher: rejestracja zakończona, timer bezczynności wystartował.");
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            WriteDiagLog($"OnSessionSwitch wywołane, powód: {e.Reason}");
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                TriggerLockMode();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                TriggerUnlockMode();
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            WriteDiagLog($"OnPowerModeChanged wywołane, tryb: {e.Mode}");
            if (e.Mode == PowerModes.Suspend)
            {
                TriggerLockMode();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                TriggerUnlockMode();
            }
        }

        private void TriggerLockMode()
        {
            if (isLockedOrAsleep) return;

            isLockedOrAsleep = true;
            WriteDiagLog($"TriggerLockMode: wchodzę w tryb ambientowy o {DateTime.Now:HH:mm:ss.fff}");
            AmbientModeRequested?.Invoke(SystemAmbientTrigger.LockOrSleep);
        }

        private void TriggerUnlockMode()
        {
            if (!isLockedOrAsleep) return;

            isLockedOrAsleep = false;
            WriteDiagLog($"TriggerUnlockMode: wychodzę z trybu ambientowego o {DateTime.Now:HH:mm:ss.fff}");

            if (!isIdleTriggered)
            {
                NormalModeRequested?.Invoke();
            }
        }

        private void CheckIdleState(object? state)
        {
            // Blokada ekranu ma priorytet i jest teraz wykrywana zdarzeniowo (WM_WTSSESSION_CHANGE) -
            // ten timer obsługuje wyłącznie logikę bezczynności.
            if (isLockedOrAsleep) return;

            // FIX: watcher wcześniej ignorował flagę IsEnabled trybu bezczynności - nawet z wyłączonym
            // przełącznikiem "Bezczynność" w Ustawieniach, po przekroczeniu IdleTimeoutMinutes i tak
            // wywoływał AmbientModeRequested, co skutkowało cyklicznym, krótkim mrugnięciem efektu WLED
            // (przez wysyłaną wcześniej czarną ramkę DDP - patrz poprawka w PipelineManager.EnterAmbientMode).
            bool isIdleAmbientEnabled = settings.IdleAmbient?.IsEnabled ?? false;

            if (!isIdleAmbientEnabled)
            {
                // Funkcja wyłączona - jeśli byliśmy w trakcie "wyzwolonego" stanu bezczynności
                // z poprzedniej konfiguracji, czyścimy go i wracamy do normalnego trybu.
                if (isIdleTriggered)
                {
                    isIdleTriggered = false;
                    NormalModeRequested?.Invoke();
                }
                return;
            }

            TimeSpan idleDuration = IdleDetector.GetIdleDuration();
            bool shouldBeIdle = idleDuration.TotalMinutes >= settings.IdleTimeoutMinutes;

            if (shouldBeIdle && !isIdleTriggered)
            {
                isIdleTriggered = true;
                WriteDiagLog($"CheckIdleState: wyzwalam tryb Idle po {idleDuration.TotalSeconds:F1}s bezczynności (próg: {settings.IdleTimeoutMinutes} min).");
                AmbientModeRequested?.Invoke(SystemAmbientTrigger.Idle);
            }
            else if (!shouldBeIdle && isIdleTriggered)
            {
                isIdleTriggered = false;
                WriteDiagLog("CheckIdleState: wychodzę z trybu Idle (wykryto aktywność).");
                NormalModeRequested?.Invoke();
            }
        }

        public void Dispose()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            idleCheckTimer.Dispose();
            messageMonitor?.Dispose();
        }
    }
}