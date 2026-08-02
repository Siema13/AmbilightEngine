using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            Profile = profile;
            TriggeringProcessName = triggeringProcessName;
        }
    }

    public sealed class ProcessProfileWatcher : IDisposable
    {
        private const int MinPollIntervalMs = 250;
        private const int SlowCycleWarningThresholdMs = 100;
        private const int HeartbeatIntervalSeconds = 5;

        private readonly TimeSpan pollInterval;
        private readonly List<AppProfile> configuredProfiles = new List<AppProfile>();
        private readonly AppProfile fallbackProfile;
        private readonly object profilesLock = new object();

        private CancellationTokenSource? cts;
        private Task? watcherTask;
        private string? lastActivatedProfileId;
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
                ColorTemperatureKelvin = 6500
            };
        }

        public void SetProfiles(IEnumerable<AppProfile>? profiles)
        {
            lock (profilesLock)
            {
                configuredProfiles.Clear();

                if (profiles == null)
                {
                    Debug.WriteLine("[DIAG] ProcessProfileWatcher: SetProfiles wywołane z null, lista profili wyczyszczona.");
                    return;
                }

                var validProfiles = new List<AppProfile>();
                foreach (AppProfile? profile in profiles)
                {
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.ExecutableFileName))
                    {
                        validProfiles.Add(profile);
                    }
                }

                validProfiles.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                configuredProfiles.AddRange(validProfiles);
            }

            Debug.WriteLine($"[DIAG] ProcessProfileWatcher: SetProfiles wywołane, liczba profili: {configuredProfiles.Count}.");
        }

        public void Start()
        {
            if (watcherTask != null && !watcherTask.IsCompleted) return;

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
                // Ignorujemy timeout/OperationCanceledException przy zamykaniu.
            }
            finally
            {
                cts?.Dispose();
                cts = null;
                watcherTask = null;
                lastActivatedProfileId = null;
            }
        }

        private void WatchLoop(CancellationToken token)
        {
            Debug.WriteLine($"[DIAG] ProcessProfileWatcher wystartował, interwał odpytywania: {pollInterval.TotalMilliseconds:F0}ms.");

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
                        Debug.WriteLine($"[DIAG] ProcessProfileWatcher: błąd podczas ewaluacji procesów - {ex.GetType().Name}: {ex.Message}");
                    }

                    token.WaitHandle.WaitOne(pollInterval);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DIAG] ProcessProfileWatcher: KRYTYCZNY błąd, pętla przerwana! {ex.GetType().Name}: {ex.Message}");
            }

            Debug.WriteLine("[DIAG] ProcessProfileWatcher zakończony.");
        }

        private void EvaluateActiveProfile()
        {
            List<AppProfile> snapshot;
            lock (profilesLock)
            {
                snapshot = configuredProfiles.Count == 0
                    ? EmptyProfileList
                    : new List<AppProfile>(configuredProfiles);
            }

            DateTime cycleStart = DateTime.Now;

            try
            {
                if (snapshot.Count == 0)
                {
                    ActivateIfChanged(fallbackProfile, "none");
                    return;
                }

                Process[] runningProcesses = Process.GetProcesses();
                try
                {
                    var runningNames = new HashSet<string>(runningProcesses.Length, StringComparer.OrdinalIgnoreCase);
                    foreach (Process proc in runningProcesses)
                    {
                        try
                        {
                            string? name = proc.ProcessName;
                            if (!string.IsNullOrEmpty(name))
                            {
                                runningNames.Add(name + ".exe");
                            }
                        }
                        catch (Exception)
                        {
                            // Proces mógł zniknąć między enumeracją i odczytem nazwy - ignorujemy.
                        }
                    }

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        AppProfile profile = snapshot[i];
                        if (profile == null) continue;

                        string? matchedName = null;
                        foreach (string runningName in runningNames)
                        {
                            if (profile.MatchesProcess(runningName))
                            {
                                matchedName = runningName;
                                break;
                            }
                        }

                        if (matchedName != null)
                        {
                            ActivateIfChanged(profile, matchedName);
                            return;
                        }
                    }

                    ActivateIfChanged(fallbackProfile, "none");
                }
                finally
                {
                    foreach (Process proc in runningProcesses)
                    {
                        try
                        {
                            proc.Dispose();
                        }
                        catch (Exception)
                        {
                            // Ignorujemy - proces mógł już być zwolniony.
                        }
                    }
                }
            }
            finally
            {
                double elapsed = (DateTime.Now - cycleStart).TotalMilliseconds;
                if (elapsed > SlowCycleWarningThresholdMs)
                {
                    Debug.WriteLine($"[DIAG] EvaluateActiveProfile trwało {elapsed:F0}ms - podejrzanie długo!");
                }
            }
        }

        private static readonly List<AppProfile> EmptyProfileList = new List<AppProfile>();

        private void ActivateIfChanged(AppProfile profile, string triggeringProcessName)
        {
            if (profile == null) return;
            if (lastActivatedProfileId == profile.ProfileId) return;

            lastActivatedProfileId = profile.ProfileId;

            try
            {
                OnProfileActivationRequested?.Invoke(this, new ProfileActivatedEventArgs(profile, triggeringProcessName));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DIAG] ProcessProfileWatcher: subskrybent zdarzenia zgłosił wyjątek - {ex.GetType().Name}: {ex.Message}");
            }

            Debug.WriteLine($"[DIAG] ProcessProfileWatcher: aktywowano profil '{profile.DisplayName ?? "(bez nazwy)"}' (wyzwolony przez: {triggeringProcessName}).");
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            Stop();
        }
    }
}