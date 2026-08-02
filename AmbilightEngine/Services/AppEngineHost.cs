using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AmbilightEngine.Core.Automation;
using AmbilightEngine.Core.Capture;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Models;
using AmbilightEngine.Core.Pipeline;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Core.SystemState;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace AmbilightEngine
{
    public sealed class AppEngineHost : IDisposable
    {
        private readonly AmbilightSettings settings;

        private WgcCaptureEngine? captureEngine;
        private ImageProcessor? imageProcessor;
        private WledDdpNetworkSender? ledSender;
        private PipelineManager? pipelineManager;
        private SystemStateWatcher? stateWatcher;
        private ProcessProfileWatcher? profileWatcher;

        private int lastCapturedWidth;
        private int lastCapturedHeight;
        private CaptureZone[]? currentZones;

        private readonly BlackBarDetectionService blackBarDetector = new BlackBarDetectionService();
        private BlackBarInsets currentInsets = BlackBarInsets.None;
        public bool IsRunning { get; private set; }
        public string CurrentProfileName { get; private set; } = "Domyślny";
        public EngineStatusInfo CurrentStatus { get; private set; } = EngineStatusInfo.Stopped("Gotowy do startu.");

        public event Action<EngineStatusInfo>? StatusChanged;
        public event Action<string>? ProfileChanged;

        public AppEngineHost(AmbilightSettings settings)
        {
            this.settings = settings;
            blackBarDetector.IsEnabled = settings.EnableBlackBarDetection;
        }

        public async Task<bool> StartAsync(IntPtr windowHandle)
        {
            if (IsRunning)
            {
                SetStatus(EngineStatusInfo.Running("Ambilight już działa."));
                return true;
            }

            SetStatus(EngineStatusInfo.Starting("Inicjalizacja przechwytywania..."));

            try
            {
                GraphicsCaptureItem? item;

                if (settings.AutoStartWithDefaultMonitor)
                {
                    try
                    {
                        IntPtr hmonitor = IntPtr.Zero;

                        if (!string.IsNullOrWhiteSpace(settings.SelectedMonitorDeviceId))
                        {
                            hmonitor = MonitorCaptureHelper.FindMonitorHandleByDeviceName(settings.SelectedMonitorDeviceId);
                        }

                        if (hmonitor == IntPtr.Zero)
                        {
                            hmonitor = MonitorCaptureHelper.GetPrimaryMonitorHandle(windowHandle);
                        }

                        item = MonitorCaptureHelper.CreateItemForMonitor(hmonitor);
                        SetStatus(EngineStatusInfo.Starting("Automatycznie wybrano monitor."));
                    }
                    catch (Exception autoEx)
                    {
                        SetStatus(EngineStatusInfo.Starting($"Automatyczny wybór monitora nie powiódł się: {autoEx.Message}. Otwieram wybór ręczny..."));

                        var fallbackPicker = new GraphicsCapturePicker();
                        InitializeWithWindow.Initialize(fallbackPicker, windowHandle);
                        item = await fallbackPicker.PickSingleItemAsync();
                    }
                }
                else
                {
                    SetStatus(EngineStatusInfo.Starting("Oczekiwanie na wybór źródła przechwytywania..."));

                    var picker = new GraphicsCapturePicker();
                    InitializeWithWindow.Initialize(picker, windowHandle);
                    item = await picker.PickSingleItemAsync();
                }

                if (item == null)
                {
                    SetStatus(EngineStatusInfo.Stopped("Nie wybrano źródła przechwytywania."));
                    return false;
                }

                lastCapturedWidth = item.Size.Width;
                lastCapturedHeight = item.Size.Height;

                var zones = BuildZones(lastCapturedWidth, lastCapturedHeight);
                currentZones = zones;

                imageProcessor = new ImageProcessor(zones);
                imageProcessor.SetDynamics(
                    (float)settings.MotionAttackSpeed,
                    (float)settings.MotionDecaySpeed,
                    (float)settings.ColorSensitivity,
                    settings.MinimumBrightnessFloor);
                imageProcessor.SetQuality(settings.PixelSkipStep);
                ApplyColorCalibrationToProcessor(imageProcessor);

                ledSender?.Dispose();
                ledSender = new WledDdpNetworkSender(settings.EspIpAddress, zones.Length);

                captureEngine?.Dispose();
                captureEngine = new WgcCaptureEngine();

                pipelineManager?.Dispose();
                pipelineManager = new PipelineManager(captureEngine, imageProcessor, ledSender, settings, zones.Length);
                pipelineManager.SetBlackBarDetectionEnabled(settings.EnableBlackBarDetection);
                pipelineManager.BlackBarInsetsChanged += OnBlackBarInsetsChanged;
                pipelineManager.Start();

                captureEngine.Start(item);

                stateWatcher?.Dispose();
                stateWatcher = new SystemStateWatcher(settings);
                stateWatcher.AmbientModeRequested += trigger =>
                {
                    var mode = trigger == SystemAmbientTrigger.LockOrSleep ? settings.LockScreenMode : settings.IdleMode;
                    pipelineManager?.EnterAmbientMode(mode);
                    SetStatus(EngineStatusInfo.Ambient($"Tryb ambientowy: {trigger} ({mode})"));
                };
                stateWatcher.NormalModeRequested += () =>
                {
                    pipelineManager?.ExitAmbientMode();
                    SetStatus(EngineStatusInfo.Running("Przechwytywanie aktywne."));
                };

                InitializeProfileWatcher();
                ActivateDefaultProfile("start silnika");

                IsRunning = true;
                SetStatus(EngineStatusInfo.Running($"Aktywne: {item.DisplayName} -> {settings.EspIpAddress}"));
                return true;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                SetStatus(EngineStatusInfo.Error($"Nie udało się uruchomić Ambilight: {ex.Message}"));
                return false;
            }
        }

        public void ActivateProfile(AppProfile profile, string triggerSource)
        {
            if (profile == null || currentZones == null || pipelineManager == null)
            {
                return;
            }

            var newImageProcessor = new ImageProcessor(currentZones);
            if (imageProcessor != null)
            {
                newImageProcessor.SeedState(imageProcessor);
            }

            newImageProcessor.ApplyDspParameters(
                profile.BrightnessPercent,
                profile.SaturationBoost,
                profile.SmoothingSpeedMs,
                profile.BlackCutoffThreshold,
                profile.ColorTemperatureKelvin,
                profile.GammaValue);

            pipelineManager.ReplaceImageProcessor(newImageProcessor);
            imageProcessor = newImageProcessor;

            CurrentProfileName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "Domyślny"
                : profile.DisplayName;

            SetStatus(EngineStatusInfo.Running($"Profil aktywny: {CurrentProfileName} (źródło: {triggerSource})"));
            ProfileChanged?.Invoke(CurrentProfileName);
        }

        public void ActivateDefaultProfile(string triggerSource = "domyślny")
        {
            var defaultProfile = settings.DefaultProfile ?? new AppProfile
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

            ActivateProfile(defaultProfile, triggerSource);
        }

        public void ApplyGeometrySettings()
        {
            if (!IsRunning || lastCapturedWidth == 0 || lastCapturedHeight == 0)
            {
                SetStatus(EngineStatusInfo.Stopped("Geometria zapisana. Zostanie zastosowana przy następnym starcie."));
                return;
            }

            try
            {
                var zones = BuildZones(lastCapturedWidth, lastCapturedHeight);
                currentZones = zones;

                int previousLedCount = ledSender?.LedCount ?? -1;

                if (zones.Length == previousLedCount)
                {
                    var newProcessor = new ImageProcessor(zones);
                    newProcessor.SetDynamics(
                        (float)settings.MotionAttackSpeed,
                        (float)settings.MotionDecaySpeed,
                        (float)settings.ColorSensitivity,
                        settings.MinimumBrightnessFloor);
                    newProcessor.SetQuality(settings.PixelSkipStep);

                    ApplyProfileOrCalibration(newProcessor);

                    pipelineManager?.ReplaceImageProcessor(newProcessor);
                    imageProcessor = newProcessor;

                    SetStatus(EngineStatusInfo.Running("Geometria została zastosowana na żywo."));
                }
                else
                {
                    var newProcessor = new ImageProcessor(zones);
                    newProcessor.SetDynamics(
                        (float)settings.MotionAttackSpeed,
                        (float)settings.MotionDecaySpeed,
                        (float)settings.ColorSensitivity,
                        settings.MinimumBrightnessFloor);
                    newProcessor.SetQuality(settings.PixelSkipStep);

                    ApplyProfileOrCalibration(newProcessor);

                    imageProcessor = newProcessor;

                    ledSender?.Dispose();
                    ledSender = new WledDdpNetworkSender(settings.EspIpAddress, zones.Length);

                    pipelineManager?.Dispose();
                    pipelineManager = new PipelineManager(captureEngine!, imageProcessor, ledSender, settings, zones.Length);
                    pipelineManager.SetBlackBarDetectionEnabled(settings.EnableBlackBarDetection);
                    pipelineManager.BlackBarInsetsChanged += OnBlackBarInsetsChanged;
                    pipelineManager.Start();

                    SetStatus(EngineStatusInfo.Running($"Geometria została zastosowana na żywo. Nowa liczba diod: {zones.Length}."));
                }
            }
            catch (Exception ex)
            {
                SetStatus(EngineStatusInfo.Error($"Błąd podczas zastosowania geometrii: {ex.Message}"));
            }
        }

        private void ApplyProfileOrCalibration(ImageProcessor newProcessor)
        {
            if (!string.IsNullOrWhiteSpace(CurrentProfileName) &&
                !string.Equals(CurrentProfileName, "Domyślny", StringComparison.OrdinalIgnoreCase))
            {
                var activeProfile = settings.Profiles?.Find(p =>
                    string.Equals(p.DisplayName, CurrentProfileName, StringComparison.OrdinalIgnoreCase));

                if (activeProfile != null)
                {
                    newProcessor.ApplyDspParameters(
                        activeProfile.BrightnessPercent,
                        activeProfile.SaturationBoost,
                        activeProfile.SmoothingSpeedMs,
                        activeProfile.BlackCutoffThreshold,
                        activeProfile.ColorTemperatureKelvin,
                        activeProfile.GammaValue);
                    return;
                }
            }

            ApplyColorCalibrationToProcessor(newProcessor);
        }

        private CaptureZone[] BuildZones(int width, int height, BlackBarInsets insets = default)
        {
            int effectiveWidth = width - insets.Left - insets.Right;
            int effectiveHeight = height - insets.Top - insets.Bottom;

            if (effectiveWidth <= 0) effectiveWidth = width;
            if (effectiveHeight <= 0) effectiveHeight = height;

            if (settings.UseCustomZoneLayout)
            {
                return ZoneMapGenerator.Generate(
                    effectiveWidth, effectiveHeight,
                    settings.TopLedCount, settings.BottomLedCount,
                    settings.LeftLedCount, settings.RightLedCount,
                    settings.SamplingDepth,
                    settings.ZoneStartCorner, settings.ZoneStripDirection,
                    settings.ZoneShiftOffset, settings.ExcludedLedIndices);
            }

            return ZoneMapGenerator.Generate(effectiveWidth, effectiveHeight, settings.LedCount, settings.SamplingDepth);
        }

        private void OnBlackBarInsetsChanged(BlackBarInsets insets)
        {
            currentInsets = insets;

            if (!IsRunning || lastCapturedWidth == 0 || lastCapturedHeight == 0)
            {
                return;
            }

            try
            {
                var zones = BuildZones(lastCapturedWidth, lastCapturedHeight, currentInsets);
                currentZones = zones;

                var newProcessor = new ImageProcessor(zones);
                newProcessor.SetDynamics(
                    (float)settings.MotionAttackSpeed,
                    (float)settings.MotionDecaySpeed,
                    (float)settings.ColorSensitivity,
                    settings.MinimumBrightnessFloor);
                newProcessor.SetQuality(settings.PixelSkipStep);
                ApplyColorCalibrationToProcessor(newProcessor);

                pipelineManager?.ReplaceImageProcessor(newProcessor);
                imageProcessor = newProcessor;
            }
            catch (Exception)
            {
                // Błąd przy przeliczaniu geometrii po wykryciu czarnych pasów nie może zabić silnika.
            }
        }

        public void SetBlackBarDetectionEnabled(bool enabled)
        {
            blackBarDetector.IsEnabled = enabled;
            pipelineManager?.SetBlackBarDetectionEnabled(enabled);
        }

        private void InitializeProfileWatcher()
        {
            Debug.WriteLine($"[DIAG] EvaluateActiveProfile wywołane o {DateTime.Now:HH:mm:ss.fff}");

            if (profileWatcher != null)
            {
                return;
            }

            var defaultProfile = settings.DefaultProfile ?? new AppProfile
            {
                DisplayName = "Domyślny",
                IsBuiltInDefault = true
            };

            profileWatcher = new ProcessProfileWatcher(defaultProfile, TimeSpan.FromMilliseconds(500));
            profileWatcher.SetProfiles(settings.Profiles);
            profileWatcher.OnProfileActivationRequested += OnProfileActivationRequested;
            profileWatcher.Start();
        }

        public void RefreshProfileList()
        {
            profileWatcher?.SetProfiles(settings.Profiles);

            if (IsRunning)
            {
                ActivateDefaultProfile("odświeżenie listy profili");
            }
        }

        private void OnProfileActivationRequested(object? sender, ProfileActivatedEventArgs e)
        {
            if (currentZones == null || pipelineManager == null)
            {
                Debug.WriteLine("[DIAG] AppEngineHost: profil zignorowany - potok nie jest jeszcze aktywny.");
                return;
            }

            ActivateProfile(e.Profile, e.TriggeringProcessName);
            Debug.WriteLine($"[DIAG] AppEngineHost: zastosowano profil '{e.Profile.DisplayName}' wyzwolony przez {e.TriggeringProcessName}.");
        }

        private void ApplyColorCalibrationToProcessor(ImageProcessor processor)
        {
            var profile = settings.DefaultProfile;
            if (profile == null)
            {
                return;
            }

            processor.ApplyColorCalibration(
                profile.BrightnessPercent,
                profile.SaturationBoost,
                profile.BlackCutoffThreshold,
                profile.ColorTemperatureKelvin,
                profile.GammaValue);
        }

        public void ApplyLiveColorCalibration()
        {
            if (imageProcessor == null)
            {
                return;
            }

            ApplyColorCalibrationToProcessor(imageProcessor);

            if (string.Equals(CurrentProfileName, "Domyślny", StringComparison.OrdinalIgnoreCase))
            {
                ProfileChanged?.Invoke(CurrentProfileName);
            }
        }

        public async Task<bool> ActivateWledEffectAsync(
            int fxId,
            int speed,
            int intensity,
            int paletteId = 0,
            (byte R, byte G, byte B)? primaryColor = null,
            (byte R, byte G, byte B)? secondaryColor = null,
            int? brightness = null)
        {
            if (ledSender == null) return false;

            settings.ActiveDisplayMode = DisplayMode.WledEffects;
            return await ledSender.SetEffectAsync(fxId, speed, intensity, paletteId, primaryColor, secondaryColor, brightness);
        }

        public async Task<List<string>> GetAvailableWledEffectsAsync()
        {
            if (ledSender is WledDdpNetworkSender wledSender)
            {
                return await wledSender.GetAvailableEffectsAsync();
            }

            return new List<string>();
        }

        public async Task<List<string>> GetAvailableWledPalettesAsync()
        {
            if (ledSender is WledDdpNetworkSender wledSender)
            {
                return await wledSender.GetAvailablePalettesAsync();
            }

            return new List<string>();
        }

        public double CaptureFps => pipelineManager?.CurrentCaptureFps ?? 0;
        public double SendFps => pipelineManager?.CurrentSendFps ?? 0;

        public void ApplyLiveSettings()
        {
            imageProcessor?.SetDynamics(
                (float)settings.MotionAttackSpeed,
                (float)settings.MotionDecaySpeed,
                (float)settings.ColorSensitivity,
                settings.MinimumBrightnessFloor);
            imageProcessor?.SetQuality(settings.PixelSkipStep);
        }

        public void Stop()
        {
            try
            {
                profileWatcher?.Dispose();
                profileWatcher = null;

                stateWatcher?.Dispose();
                stateWatcher = null;

                pipelineManager?.Dispose();
                pipelineManager = null;

                captureEngine?.Dispose();
                captureEngine = null;

                imageProcessor = null;
                currentZones = null;

                IsRunning = false;
                CurrentProfileName = "Domyślny";
                ProfileChanged?.Invoke(CurrentProfileName);

                SetStatus(EngineStatusInfo.Stopped("Ambilight został zatrzymany."));
            }
            catch (Exception ex)
            {
                IsRunning = false;
                SetStatus(EngineStatusInfo.Error($"Wystąpił błąd podczas zatrzymywania: {ex.Message}"));
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void SetStatus(EngineStatusInfo status)
        {
            CurrentStatus = status;
            StatusChanged?.Invoke(status);
        }
    }
}