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
        private static readonly string DiagLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "ambilight_diag.log");

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

        // NOWOŚĆ: cache ostatniego wyniku sprawdzenia odtwarzania multimediów - CheckIdleState
        // jest synchronicznym callbackiem Timera, a sprawdzenie GlobalSystemMediaTransport-
        // ControlsSessionManager jest asynchroniczne. Odpytujemy je w tle (fire-and-forget,
        // co 2s razem z resztą logiki) i korzystamy z ostatniego znanego wyniku - unikamy
        // blokowania wątku Timera na oczekiwaniu na wolne, natywne WinRT API.
        private volatile bool isMediaCurrentlyPlaying;

        public event Action<SystemAmbientTrigger>? AmbientModeRequested;
        public event Action? NormalModeRequested;
        public event Action? SystemResumeRequested;
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
                return;
            }

            if (e.Mode == PowerModes.Resume)
            {
                TriggerUnlockMode();
            }
        }
        private void TriggerSystemResume()
        {
            isLockedOrAsleep = false;

            WriteDiagLog(
                $"TriggerSystemResume: wykryto wybudzenie systemu o {DateTime.Now:HH:mm:ss.fff}. " +
                "Oczekuję 3 s na gotowość monitora, sieci i WLED.");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));

                    if (isLockedOrAsleep)
                    {
                        WriteDiagLog(
                            "TriggerSystemResume: system ponownie wszedł w blokadę/uśpienie; recovery anulowane.");

                        return;
                    }

                    WriteDiagLog(
                        "TriggerSystemResume: zgłaszam dedykowane odtworzenie po wybudzeniu.");

                    SystemResumeRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    WriteDiagLog(
                        $"TriggerSystemResume: błąd opóźnionego recovery: {ex.Message}");
                }
            });
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
            if (!isLockedOrAsleep)
            {
                return;
            }

            isLockedOrAsleep = false;
            WriteDiagLog(
                $"TriggerUnlockMode: wykryto wybudzenie/odblokowanie o {DateTime.Now:HH:mm:ss.fff}. " +
                "Oczekuję 3 s na gotowość sieci i WLED.");

            if (isIdleTriggered)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));

                    if (isLockedOrAsleep || isIdleTriggered)
                    {
                        return;
                    }

                    WriteDiagLog("TriggerUnlockMode: zgłaszam powrót do normalnego trybu.");
                    NormalModeRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    WriteDiagLog($"TriggerUnlockMode: błąd opóźnionego powrotu: {ex.Message}");
                }
            });
        }

        private void CheckIdleState(object? state)
        {
            // Blokada ekranu ma priorytet i jest teraz wykrywana zdarzeniowo (WM_WTSSESSION_CHANGE) -
            // ten timer obsługuje wyłącznie logikę bezczynności.
            if (isLockedOrAsleep) return;

            // NOWOŚĆ: odpytujemy stan odtwarzania multimediów w tle (fire-and-forget) -
            // wynik trafia do isMediaCurrentlyPlaying i jest używany w TEJ konkretnej
            // iteracji (może być o jeden cykl "spóźniony", co jest akceptowalne przy 2s
            // interwale). Nie blokujemy wątku Timera oczekiwaniem na wolne WinRT API.
            _ = RefreshMediaPlaybackStateAsync();

            // FIX: watcher wcześniej ignorował flagę IsEnabled trybu bezczynności - nawet z wyłączonym
            // przełącznikiem "Bezczynność" w Ustawieniach, po przekroczeniu IdleTimeoutMinutes i tak
            // wywoływał AmbientModeRequested, co skutkowało cyklicznym, krótkim mrugnięciem efektu WLED
            // (przez wysyłaną wcześniej czarną ramkę DDP - patrz poprawka w PipelineManager.EnterAmbientMode).
            bool isIdleAmbientEnabled = settings.IdleAmbient?.IsEnabled ?? false;

            if (!isIdleAmbientEnabled)
            {
                if (isIdleTriggered)
                {
                    isIdleTriggered = false;
                    NormalModeRequested?.Invoke();
                }
                return;
            }

            // NOWOŚĆ: jeśli jakakolwiek aplikacja w systemie aktywnie odtwarza multimedia
            // (film, muzyka), NIE przechodzimy w tryb bezczynności niezależnie od tego, jak
            // długo użytkownik nie rusza myszką/klawiaturą - typowa sytuacja przy oglądaniu
            // filmu na fullscreenie. Jeśli byliśmy już w trybie Idle, wychodzimy z niego.
            if (isMediaCurrentlyPlaying)
            {
                if (isIdleTriggered)
                {
                    isIdleTriggered = false;
                    WriteDiagLog("CheckIdleState: wychodzę z trybu Idle (wykryto aktywne odtwarzanie multimediów).");
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

        private async System.Threading.Tasks.Task RefreshMediaPlaybackStateAsync()
        {
            isMediaCurrentlyPlaying = await MediaPlaybackDetector.IsAnyMediaCurrentlyPlayingAsync();
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