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

    // Nasłuchuje zdarzeń systemowych Windows (blokada, uśpienie, bezczynność, zamknięcie systemu)
    // i zgłasza zdarzenie, gdy aplikacja powinna przełączyć się w tryb ambientowy, wrócić do normalnej
    // pracy, przywrócić stan po wybudzeniu, albo trwale wygasić i zamknąć połączenie z urządzeniem
    // wyjściowym przed shutdownem/restartem/wylogowaniem.
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

        // Kluczowe zabezpieczenie: cała logika wewnętrzna (CheckIdleState, TriggerLockMode,
        // TriggerUnlockMode, OnSessionEnding) jest chroniona tym samym lockiem co Dispose().
        // Eliminuje to race condition, w którym Timer.CheckIdleState (wątek z ThreadPool) albo
        // callback WtsSessionMessageMonitor mógłby wystrzelić zdarzenie w kierunku już
        // zdysponowanego PipelineManager/AppEngineHost, powodując ObjectDisposedException.
        private readonly object disposeLock = new object();

        private volatile bool isDisposed;

        // Ustawiana PRZED wywołaniem SystemShutdownRequested - blokuje wszystkie inne ścieżki
        // (idle, lock/unlock, resume) przed kolizją z sekwencją gaszenia diod przy zamykaniu
        // systemu. Windows daje aplikacji tylko kilka sekund w handlerze SessionEnding, więc
        // ta flaga musi być ustawiana synchronicznie i natychmiast.
        private volatile bool isShuttingDown;

        // NOWOŚĆ: cache ostatniego wyniku sprawdzenia odtwarzania multimediów - CheckIdleState
        // jest synchronicznym callbackiem Timera, a sprawdzenie GlobalSystemMediaTransport-
        // ControlsSessionManager jest asynchroniczne. Odpytujemy je w tle (fire-and-forget,
        // co 2s razem z resztą logiki) i korzystamy z ostatniego znanego wyniku - unikamy
        // blokowania wątku Timera na oczekiwaniu na wolne, natywne WinRT API.
        private volatile bool isMediaCurrentlyPlaying;

        public event Action<SystemAmbientTrigger>? AmbientModeRequested;
        public event Action? NormalModeRequested;
        public event Action? SystemResumeRequested;

        // Zgłaszane, gdy Windows kończy sesję (wyłączenie, restart, wylogowanie). Handler tego
        // zdarzenia powinien działać jak najszybciej i w miarę możliwości synchronicznie - system
        // daje aplikacji tylko kilka sekund, zanim ubije proces bez dalszego ostrzeżenia.
        public event Action? SystemShutdownRequested;

        // hwnd MUSI być realnym uchwytem głównego okna aplikacji (WindowNative.GetWindowHandle) -
        // jest niezbędny do podczepienia się pod komunikat WM_WTSSESSION_CHANGE.
        public SystemStateWatcher(AmbilightSettings settings, IntPtr windowHandle)
        {
            WriteDiagLog("SystemStateWatcher: konstruktor wywołany, rejestruję zdarzenia.");
            this.settings = settings;

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;

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
            if (isDisposed || isShuttingDown) return;

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
            if (isDisposed || isShuttingDown) return;

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

        // Zgłaszane przez Windows przy Shutdown, Restart oraz Logoff - w przeciwieństwie do
        // PowerModes.Suspend (uśpienie), z którego system może się wybudzić, ta ścieżka zakłada,
        // że proces zostanie wkrótce ubity i NIE należy próbować przywracać żadnego stanu - należy
        // trwale wygasić diody i zamknąć połączenie sieciowe z WLED.
        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            lock (disposeLock)
            {
                if (isDisposed || isShuttingDown) return;

                WriteDiagLog($"OnSessionEnding wywołane, powód: {e.Reason}");
                isShuttingDown = true;
            }

            // Wywołanie poza lockiem - handler w AppEngineHost wykonuje operacje I/O (wysyłka
            // ramki DDP, zamknięcie gniazda UDP) i nie powinien blokować wewnętrznej blokady
            // watchera na czas trwania tej operacji.
            SystemShutdownRequested?.Invoke();
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
            lock (disposeLock)
            {
                if (isDisposed || isShuttingDown) return;
                if (isLockedOrAsleep) return;

                isLockedOrAsleep = true;
                WriteDiagLog($"TriggerLockMode: wchodzę w tryb ambientowy o {DateTime.Now:HH:mm:ss.fff}");
                AmbientModeRequested?.Invoke(SystemAmbientTrigger.LockOrSleep);
            }
        }

        private void TriggerUnlockMode()
        {
            lock (disposeLock)
            {
                if (isDisposed || isShuttingDown) return;
                if (!isLockedOrAsleep) return;

                TriggerUnlockModeInternal();
            }
        }

        // Musi być wywoływane wewnątrz disposeLock - wyodrębnione, by TriggerUnlockMode mógł sam
        // sprawdzić warunek isLockedOrAsleep przed wejściem w logikę opóźnionego powrotu.
        private void TriggerUnlockModeInternal()
        {
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

                    lock (disposeLock)
                    {
                        if (isDisposed || isShuttingDown || isLockedOrAsleep || isIdleTriggered)
                        {
                            return;
                        }

                        WriteDiagLog("TriggerUnlockMode: zgłaszam powrót do normalnego trybu.");
                        NormalModeRequested?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    WriteDiagLog($"TriggerUnlockMode: błąd opóźnionego powrotu: {ex.Message}");
                }
            });
        }

        private void CheckIdleState(object? state)
        {
            lock (disposeLock)
            {
                // Kluczowe zabezpieczenie: jeśli Dispose() już się rozpoczął (lub zakończył),
                // albo system jest w trakcie zamykania, ten callback natychmiast się wycofuje
                // i nie odpala żadnych zdarzeń w kierunku już zdysponowanego/gaszonego pipeline'u.
                if (isDisposed || isShuttingDown) return;

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
        }

        private async System.Threading.Tasks.Task RefreshMediaPlaybackStateAsync()
        {
            isMediaCurrentlyPlaying = await MediaPlaybackDetector.IsAnyMediaCurrentlyPlayingAsync();
        }

        public void Dispose()
        {
            lock (disposeLock)
            {
                if (isDisposed) return;
                isDisposed = true;

                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionEnding -= OnSessionEnding;
            }

            // Czekamy, aż ewentualny aktualnie wykonujący się callback timera zakończy działanie,
            // zanim zwolnimy zasoby - eliminuje to wyścig wątków będący źródłem ObjectDisposedException.
            using var waitHandle = new ManualResetEvent(false);
            idleCheckTimer.Dispose(waitHandle);
            waitHandle.WaitOne(TimeSpan.FromSeconds(2));

            messageMonitor?.Dispose();
        }
    }
}