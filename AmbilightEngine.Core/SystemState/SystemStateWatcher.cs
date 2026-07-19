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
    public sealed class SystemStateWatcher : IDisposable
    {
        private readonly AmbilightSettings settings;
        private readonly Timer idleCheckTimer;
        private bool isLockedOrAsleep;
        private bool isIdleTriggered;

        public event Action<SystemAmbientTrigger>? AmbientModeRequested;
        public event Action? NormalModeRequested;

        public SystemStateWatcher(AmbilightSettings settings)
        {
            System.Diagnostics.Debug.WriteLine("[DIAG] SystemStateWatcher: konstruktor wywołany, rejestruję zdarzenia.");
            this.settings = settings;

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            idleCheckTimer = new Timer(CheckIdleState, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            System.Diagnostics.Debug.WriteLine("[DIAG] SystemStateWatcher: rejestracja zakończona, timer bezczynności wystartował.");
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[DIAG] OnSessionSwitch wywołane, powód: {e.Reason}");
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                isLockedOrAsleep = true;
                AmbientModeRequested?.Invoke(SystemAmbientTrigger.LockOrSleep);
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                isLockedOrAsleep = false;
                if (!isIdleTriggered)
                {
                    NormalModeRequested?.Invoke();
                }
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[DIAG] OnPowerModeChanged wywołane, tryb: {e.Mode}");
            if (e.Mode == PowerModes.Suspend)
            {
                isLockedOrAsleep = true;
                AmbientModeRequested?.Invoke(SystemAmbientTrigger.LockOrSleep);
            }
            else if (e.Mode == PowerModes.Resume)
            {
                isLockedOrAsleep = false;
                if (!isIdleTriggered)
                {
                    NormalModeRequested?.Invoke();
                }
            }
        }

        private void CheckIdleState(object? state)
        {
            if (isLockedOrAsleep) return;

            TimeSpan idleDuration = IdleDetector.GetIdleDuration();
            bool shouldBeIdle = idleDuration.TotalMinutes >= settings.IdleTimeoutMinutes;

            if (shouldBeIdle && !isIdleTriggered)
            {
                isIdleTriggered = true;
                AmbientModeRequested?.Invoke(SystemAmbientTrigger.Idle);
            }
            else if (!shouldBeIdle && isIdleTriggered)
            {
                isIdleTriggered = false;
                NormalModeRequested?.Invoke();
            }
        }

        public void Dispose()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            idleCheckTimer.Dispose();
        }
    }
}