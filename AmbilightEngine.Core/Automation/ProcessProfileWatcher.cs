using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Models;

namespace AmbilightEngine.Core.Automation
{
    public sealed class ProfileActivatedEventArgs : EventArgs
    {
        public AppProfile Profile { get; }
        public string TriggeringProcessName { get; }

        public ProfileActivatedEventArgs(AppProfile profile, string triggeringProcessName)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            TriggeringProcessName = triggeringProcessName ?? string.Empty;
        }
    }

    public sealed class ProcessProfileWatcher : IDisposable
    {
        private const int MinPollIntervalMs = 250;
        private const int SlowCycleWarningThresholdMs = 100;
        private const int HeartbeatIntervalSeconds = 5;
        private const int ProfileSwitchDebounceMs = 750;

        private readonly TimeSpan pollInterval;
        private readonly List<AppProfile> configuredProfiles = new List<AppProfile>();
        private readonly AppProfile fallbackProfile;
        private readonly object profilesLock = new object();

        private CancellationTokenSource? cts;
        private Task? watcherTask;

        private string? lastActivatedProfileId;
        private string? pendingProfileId;
        private DateTime pendingProfileSinceUtc;
        private bool isDisposed;
        private DateTime lastHeartbeat = DateTime.MinValue;

        public event EventHandler<ProfileActivatedEventArgs>? OnProfileActivationRequested;

        public ProcessProfileWatcher(AppProfile fallbackProfile, TimeSpan? pollInterval = null)
        {
            this.fallbackProfile = fallbackProfile ?? CreateSafeFallback();

            TimeSpan requestedInterval = pollInterval ?? TimeSpan.FromMilliseconds(750);
            this.pollInterval = requestedInterval.TotalMilliseconds < MinPollIntervalMs
                ? TimeSpan.FromMilliseconds(MinPollIntervalMs)
                : requestedInterval;
        }

        public void SetProfiles(IEnumerable<AppProfile>? profiles)
        {
            int profileCount;

            lock (profilesLock)
            {
                configuredProfiles.Clear();

                if (profiles == null)
                {
                    profileCount = 0;
                }
                else
                {
                    foreach (AppProfile? profile in profiles)
                    {
                        if (profile != null &&
                            !string.IsNullOrWhiteSpace(profile.ExecutableFileName))
                        {
                            configuredProfiles.Add(profile);
                        }
                    }

                    configuredProfiles.Sort(CompareProfiles);
                    profileCount = configuredProfiles.Count;
                }
            }

            ResetPendingCandidate();

            Debug.WriteLine(
                $"[DIAG] ProcessProfileWatcher: SetProfiles wywołane, liczba profili: {profileCount}.");
        }

        public void Start()
        {
            ThrowIfDisposed();

            if (watcherTask != null && !watcherTask.IsCompleted)
            {
                return;
            }

            cts?.Dispose();
            cts = new CancellationTokenSource();

            watcherTask = Task.Factory.StartNew(
                () => WatchLoop(cts.Token),
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            try
            {
                cts?.Cancel();
                watcherTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // Przy zamykaniu ignorujemy timeout i anulowanie zadania.
            }
            finally
            {
                cts?.Dispose();
                cts = null;
                watcherTask = null;
                lastActivatedProfileId = null;
                ResetPendingCandidate();
            }
        }

        private void WatchLoop(CancellationToken token)
        {
            Debug.WriteLine(
                $"[DIAG] ProcessProfileWatcher wystartował, interwał odpytywania: {pollInterval.TotalMilliseconds:F0} ms.");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= HeartbeatIntervalSeconds)
                    {
                        lastHeartbeat = DateTime.UtcNow;
                        Debug.WriteLine("[DIAG] ProcessProfileWatcher: żyję, sprawdzam procesy...");
                    }

                    try
                    {
                        EvaluateActiveProfile();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[DIAG] ProcessProfileWatcher: błąd podczas ewaluacji procesów - {ex.GetType().Name}: {ex.Message}");
                    }

                    token.WaitHandle.WaitOne(pollInterval);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] ProcessProfileWatcher: KRYTYCZNY błąd, pętla przerwana! {ex.GetType().Name}: {ex.Message}");
            }

            Debug.WriteLine("[DIAG] ProcessProfileWatcher zakończony.");
        }

        private void EvaluateActiveProfile()
        {
            List<AppProfile> profilesSnapshot = GetProfilesSnapshot();
            DateTime cycleStart = DateTime.UtcNow;

            try
            {
                if (profilesSnapshot.Count == 0)
                {
                    RequestActivation(fallbackProfile, "fallback: brak skonfigurowanych profili");
                    return;
                }

                string? foregroundProcessName = TryGetForegroundProcessName();

                if (!string.IsNullOrWhiteSpace(foregroundProcessName))
                {
                    AppProfile? foregroundProfile = FindFirstMatchingProfile(
                        profilesSnapshot,
                        foregroundProcessName);

                    if (foregroundProfile != null)
                    {
                        RequestActivation(
                            foregroundProfile,
                            $"aktywne okno: {foregroundProcessName}");

                        return;
                    }
                }

                string? backgroundProcessName;
                AppProfile? backgroundProfile = FindBestBackgroundProfile(
                    profilesSnapshot,
                    out backgroundProcessName);

                if (backgroundProfile != null &&
                    !string.IsNullOrWhiteSpace(backgroundProcessName))
                {
                    RequestActivation(
                        backgroundProfile,
                        $"proces w tle: {backgroundProcessName}");

                    return;
                }

                RequestActivation(fallbackProfile, "fallback: brak pasującego procesu");
            }
            finally
            {
                double elapsedMs = (DateTime.UtcNow - cycleStart).TotalMilliseconds;

                if (elapsedMs > SlowCycleWarningThresholdMs)
                {
                    Debug.WriteLine(
                        $"[DIAG] EvaluateActiveProfile trwało {elapsedMs:F0} ms - podejrzanie długo.");
                }
            }
        }

        private List<AppProfile> GetProfilesSnapshot()
        {
            lock (profilesLock)
            {
                return configuredProfiles.Count == 0
                    ? new List<AppProfile>()
                    : new List<AppProfile>(configuredProfiles);
            }
        }

        private static AppProfile? FindFirstMatchingProfile(
            IReadOnlyList<AppProfile> profiles,
            string processName)
        {
            for (int index = 0; index < profiles.Count; index++)
            {
                AppProfile profile = profiles[index];

                if (profile.MatchesProcess(processName))
                {
                    return profile;
                }
            }

            return null;
        }

        private static AppProfile? FindBestBackgroundProfile(
            IReadOnlyList<AppProfile> profiles,
            out string? matchedProcessName)
        {
            matchedProcessName = null;
            Process[] runningProcesses = Process.GetProcesses();

            try
            {
                var runningProcessNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (Process process in runningProcesses)
                {
                    try
                    {
                        string processName = process.ProcessName;

                        if (!string.IsNullOrWhiteSpace(processName))
                        {
                            runningProcessNames.Add(processName + ".exe");
                        }
                    }
                    catch (Exception)
                    {
                        // Proces mógł zakończyć działanie między enumeracją a odczytem nazwy.
                    }
                }

                for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
                {
                    AppProfile profile = profiles[profileIndex];

                    if (!profile.AllowBackgroundActivation)
                    {
                        continue;
                    }

                    foreach (string processName in runningProcessNames)
                    {
                        if (profile.MatchesProcess(processName))
                        {
                            matchedProcessName = processName;
                            return profile;
                        }
                    }
                }

                return null;
            }
            finally
            {
                foreach (Process process in runningProcesses)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception)
                    {
                        // Proces mógł już zostać zwolniony przez system.
                    }
                }
            }
        }

        private void RequestActivation(AppProfile candidateProfile, string triggerSource)
        {
            if (candidateProfile == null)
            {
                return;
            }

            if (string.Equals(
                    lastActivatedProfileId,
                    candidateProfile.ProfileId,
                    StringComparison.Ordinal))
            {
                ResetPendingCandidate();
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;

            if (!string.Equals(
                    pendingProfileId,
                    candidateProfile.ProfileId,
                    StringComparison.Ordinal))
            {
                pendingProfileId = candidateProfile.ProfileId;
                pendingProfileSinceUtc = nowUtc;

                Debug.WriteLine(
                    $"[DIAG] ProcessProfileWatcher: kandydat '{candidateProfile.DisplayName}' " +
                    $"({triggerSource}), oczekuję {ProfileSwitchDebounceMs} ms na stabilizację.");

                return;
            }

            double stableForMs = (nowUtc - pendingProfileSinceUtc).TotalMilliseconds;

            if (stableForMs < ProfileSwitchDebounceMs)
            {
                return;
            }

            lastActivatedProfileId = candidateProfile.ProfileId;
            ResetPendingCandidate();

            try
            {
                OnProfileActivationRequested?.Invoke(
                    this,
                    new ProfileActivatedEventArgs(candidateProfile, triggerSource));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] ProcessProfileWatcher: subskrybent zdarzenia zgłosił wyjątek - {ex.GetType().Name}: {ex.Message}");
            }

            Debug.WriteLine(
                $"[DIAG] ProcessProfileWatcher: aktywowano profil " +
                $"'{candidateProfile.DisplayName ?? "(bez nazwy)"}' (wyzwolony przez: {triggerSource}).");
        }

        private void ResetPendingCandidate()
        {
            pendingProfileId = null;
            pendingProfileSinceUtc = DateTime.MinValue;
        }

        private static int CompareProfiles(AppProfile? first, AppProfile? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first == null)
            {
                return 1;
            }

            if (second == null)
            {
                return -1;
            }

            int priorityComparison = second.Priority.CompareTo(first.Priority);

            if (priorityComparison != 0)
            {
                return priorityComparison;
            }

            return string.Compare(
                first.DisplayName,
                second.DisplayName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryGetForegroundProcessName()
        {
            IntPtr foregroundWindow = GetForegroundWindow();

            if (foregroundWindow == IntPtr.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(foregroundWindow, out uint processId);

            if (processId == 0)
            {
                return null;
            }

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;

                return string.IsNullOrWhiteSpace(processName)
                    ? null
                    : processName + ".exe";
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        private static AppProfile CreateSafeFallback()
        {
            return new AppProfile
            {
                DisplayName = "Domyślny",
                IsBuiltInDefault = true,
                BrightnessPercent = 100,
                SaturationBoost = 1.0,
                SmoothingSpeedMs = 120,
                BlackCutoffThreshold = 8,
                ColorTemperatureKelvin = 6500,
                GammaValue = 2.2
            };
        }

        private void ThrowIfDisposed()
        {
            if (isDisposed)
            {
                throw new ObjectDisposedException(nameof(ProcessProfileWatcher));
            }
        }

        public void ResetActiveProfile()
        {
            lastActivatedProfileId = null;
            ResetPendingCandidate();

            Debug.WriteLine(
                "[DIAG] ProcessProfileWatcher: zresetowano aktywny profil; nastąpi ponowna ewaluacja.");
        }
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            Stop();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out uint processId);
    }
}