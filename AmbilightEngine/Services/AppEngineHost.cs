using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        public bool IsCapturing { get; private set; }

        public string CurrentProfileName { get; private set; } = "Domyślny";
        public EngineStatusInfo CurrentStatus { get; private set; } = EngineStatusInfo.Stopped("Gotowy do startu.");

        public event Action<EngineStatusInfo>? StatusChanged;
        public event Action<string>? ProfileChanged;

        public AppEngineHost(AmbilightSettings settings)
        {
            this.settings = settings;
            blackBarDetector.IsEnabled = settings.EnableBlackBarDetection;
        }

        public async Task<bool> EnsureWledConnectionAsync(IntPtr windowHandle)
        {
            if (ledSender != null)
            {
                return true;
            }

            SetStatus(EngineStatusInfo.Starting("Łączenie z urządzeniem WLED..."));

            try
            {
                var zones = BuildZones(1920, 1080);
                currentZones = zones;

                imageProcessor = new ImageProcessor(zones);
                imageProcessor.SetDynamics(
                    (float)settings.MotionAttackSpeed,
                    (float)settings.MotionDecaySpeed,
                    (float)settings.ColorSensitivity,
                    settings.MinimumBrightnessFloor);
                imageProcessor.SetQuality(settings.PixelSkipStep);
                ApplyColorCalibrationToProcessor(imageProcessor);

                ledSender = new WledDdpNetworkSender(settings.EspIpAddress, zones.Length);
                ledSender.Open();

                stateWatcher?.Dispose();
                stateWatcher = new SystemStateWatcher(settings, windowHandle);
                stateWatcher.AmbientModeRequested += trigger =>
                {
                    var config = trigger == SystemAmbientTrigger.LockOrSleep
                        ? settings.LockScreenAmbient
                        : settings.IdleAmbient;

                    pipelineManager?.EnterAmbientMode(config);
                    SetStatus(EngineStatusInfo.Ambient($"Tryb ambientowy: {trigger} (efekt WLED #{config.EffectId}, włączony: {config.IsEnabled})"));
                };
                stateWatcher.NormalModeRequested += () =>
                {
                    pipelineManager?.ExitAmbientMode();
                    SetStatus(IsCapturing
                        ? EngineStatusInfo.Running("Przechwytywanie aktywne.")
                        : EngineStatusInfo.Running("Połączono z WLED, przechwytywanie wyłączone."));
                };

                InitializeProfileWatcher();
                ActivateDefaultProfile("połączenie z WLED");

                IsRunning = true;
                SetStatus(EngineStatusInfo.Running($"Połączono z WLED: {settings.EspIpAddress}. Przechwytywanie wyłączone."));
                return true;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                SetStatus(EngineStatusInfo.Error($"Nie udało się połączyć z WLED: {ex.Message}"));
                return false;
            }
        }

        public async Task<bool> StartCaptureAsync(IntPtr windowHandle)
        {
            if (ledSender == null)
            {
                SetStatus(EngineStatusInfo.Error("Brak połączenia z WLED - nie można uruchomić przechwytywania."));
                return false;
            }

            if (IsCapturing)
            {
                SetStatus(EngineStatusInfo.Running("Przechwytywanie już aktywne."));
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
                    SetStatus(EngineStatusInfo.Running("Nie wybrano źródła - połączenie z WLED pozostaje aktywne."));
                    return false;
                }

                lastCapturedWidth = item.Size.Width;
                lastCapturedHeight = item.Size.Height;

                var zones = BuildZones(lastCapturedWidth, lastCapturedHeight);
                currentZones = zones;

                var newProcessor = new ImageProcessor(zones);
                newProcessor.SetDynamics(
                    (float)settings.MotionAttackSpeed,
                    (float)settings.MotionDecaySpeed,
                    (float)settings.ColorSensitivity,
                    settings.MinimumBrightnessFloor);
                newProcessor.SetQuality(settings.PixelSkipStep);
                ApplyProfileOrCalibration(newProcessor);
                imageProcessor = newProcessor;

                if (zones.Length != ledSender!.LedCount)
                {
                    ledSender.Reconfigure(zones.Length);
                }

                captureEngine?.Dispose();
                captureEngine = new WgcCaptureEngine();

                pipelineManager?.Dispose();
                pipelineManager = new PipelineManager(captureEngine, imageProcessor, ledSender, settings, zones.Length);
                pipelineManager.SetBlackBarDetectionEnabled(settings.EnableBlackBarDetection);
                pipelineManager.BlackBarInsetsChanged += OnBlackBarInsetsChanged;
                pipelineManager.Start();

                captureEngine.Start(item);

                IsCapturing = true;
                SetStatus(EngineStatusInfo.Running($"Aktywne: {item.DisplayName} -> {settings.EspIpAddress}"));
                return true;
            }
            catch (Exception ex)
            {
                IsCapturing = false;
                SetStatus(EngineStatusInfo.Error($"Nie udało się uruchomić przechwytywania: {ex.Message}"));
                return false;
            }
        }

        public void StopCapture()
        {
            if (!IsCapturing) return;

            try
            {
                pipelineManager?.Dispose();
                pipelineManager = null;

                captureEngine?.Dispose();
                captureEngine = null;

                IsCapturing = false;
                SetStatus(EngineStatusInfo.Running("Przechwytywanie wstrzymane. Połączenie z WLED pozostaje aktywne."));
            }
            catch (Exception ex)
            {
                IsCapturing = false;
                SetStatus(EngineStatusInfo.Error($"Błąd podczas zatrzymywania przechwytywania: {ex.Message}"));
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

            newImageProcessor.SetWallColorFromHex(settings.WallColorHex, settings.WallColorStrength);

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

            if (pipelineManager != null)
            {
                ActivateProfile(defaultProfile, triggerSource);
            }
            else
            {
                CurrentProfileName = string.IsNullOrWhiteSpace(defaultProfile.DisplayName)
                    ? "Domyślny"
                    : defaultProfile.DisplayName;
                ProfileChanged?.Invoke(CurrentProfileName);
            }
        }

        public void ApplyGeometrySettings()
        {
            if (!IsCapturing || lastCapturedWidth == 0 || lastCapturedHeight == 0)
            {
                SetStatus(EngineStatusInfo.Stopped("Geometria zapisana. Zostanie zastosowana przy następnym starcie przechwytywania."));
                return;
            }

            try
            {
                var zones = BuildZones(lastCapturedWidth, lastCapturedHeight);
                currentZones = zones;

                int previousLedCount = ledSender?.LedCount ?? -1;

                var newProcessor = new ImageProcessor(zones);
                newProcessor.SetDynamics(
                    (float)settings.MotionAttackSpeed,
                    (float)settings.MotionDecaySpeed,
                    (float)settings.ColorSensitivity,
                    settings.MinimumBrightnessFloor);
                newProcessor.SetQuality(settings.PixelSkipStep);
                ApplyProfileOrCalibration(newProcessor);
                imageProcessor = newProcessor;

                if (zones.Length != previousLedCount)
                {
                    ledSender?.Reconfigure(zones.Length);

                    pipelineManager?.Dispose();
                    pipelineManager = new PipelineManager(captureEngine!, imageProcessor, ledSender!, settings, zones.Length);
                    pipelineManager.SetBlackBarDetectionEnabled(settings.EnableBlackBarDetection);
                    pipelineManager.BlackBarInsetsChanged += OnBlackBarInsetsChanged;
                    pipelineManager.Start();

                    SetStatus(EngineStatusInfo.Running($"Geometria została zastosowana na żywo. Nowa liczba diod: {zones.Length}."));
                }
                else
                {
                    pipelineManager?.ReplaceImageProcessor(newProcessor);
                    SetStatus(EngineStatusInfo.Running("Geometria została zastosowana na żywo."));
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

                    newProcessor.SetWallColorFromHex(settings.WallColorHex, settings.WallColorStrength);
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

            if (!IsCapturing || lastCapturedWidth == 0 || lastCapturedHeight == 0)
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

            if (IsCapturing)
            {
                ActivateDefaultProfile("odświeżenie listy profili");
            }
        }

        private void OnProfileActivationRequested(object? sender, ProfileActivatedEventArgs e)
        {
            if (currentZones == null || pipelineManager == null)
            {
                Debug.WriteLine("[DIAG] AppEngineHost: profil zignorowany - potok przechwytywania nie jest aktywny.");
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

            processor.SetWallColorFromHex(settings.WallColorHex, settings.WallColorStrength);
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
            int? brightness = null,
            int? custom1 = null,
            int? custom2 = null,
            int? custom3 = null,
            bool? check1 = null,
            bool? check2 = null,
            bool? check3 = null,
            CancellationToken cancellationToken = default)
        {
            if (ledSender == null) return false;

            settings.ActiveDisplayMode = DisplayMode.WledEffects;

            settings.LastWledEffectId = fxId;
            settings.LastWledPaletteId = paletteId;
            settings.LastWledSpeed = speed;
            settings.LastWledIntensity = intensity;
            settings.LastWledBrightness = brightness ?? settings.LastWledBrightness;
            settings.LastWledCustom1 = custom1 ?? settings.LastWledCustom1;
            settings.LastWledCustom2 = custom2 ?? settings.LastWledCustom2;
            settings.LastWledCustom3 = custom3 ?? settings.LastWledCustom3;
            settings.LastWledCheck1 = check1 ?? settings.LastWledCheck1;
            settings.LastWledCheck2 = check2 ?? settings.LastWledCheck2;
            settings.LastWledCheck3 = check3 ?? settings.LastWledCheck3;

            if (primaryColor.HasValue)
            {
                settings.LastWledPrimaryColorR = primaryColor.Value.R;
                settings.LastWledPrimaryColorG = primaryColor.Value.G;
                settings.LastWledPrimaryColorB = primaryColor.Value.B;
            }

            if (secondaryColor.HasValue)
            {
                settings.LastWledSecondaryColorR = secondaryColor.Value.R;
                settings.LastWledSecondaryColorG = secondaryColor.Value.G;
                settings.LastWledSecondaryColorB = secondaryColor.Value.B;
            }

            return await ledSender.SetEffectAsync(
                fxId, speed, intensity, paletteId, primaryColor, secondaryColor, brightness,
                custom1, custom2, custom3, check1, check2, check3, cancellationToken);
        }

        public async Task<bool> PreviewWledEffectAsync(
            int fxId,
            int speed,
            int intensity,
            int paletteId = 0,
            (byte R, byte G, byte B)? primaryColor = null,
            (byte R, byte G, byte B)? secondaryColor = null,
            int? brightness = null,
            int? custom1 = null,
            int? custom2 = null,
            int? custom3 = null,
            bool? check1 = null,
            bool? check2 = null,
            bool? check3 = null,
            CancellationToken cancellationToken = default)
        {
            if (ledSender == null) return false;

            return await ledSender.SetEffectAsync(
                fxId, speed, intensity, paletteId, primaryColor, secondaryColor, brightness,
                custom1, custom2, custom3, check1, check2, check3, cancellationToken);
        }

        public async Task<List<string>> GetAvailableWledEffectsAsync()
        {
            if (ledSender != null)
            {
                return await ledSender.GetAvailableEffectsAsync();
            }

            try
            {
                using var probeSender = new WledDdpNetworkSender(settings.EspIpAddress, settings.LedCount);
                return await probeSender.GetAvailableEffectsAsync();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        public async Task<List<string>> GetAvailableWledPalettesAsync()
        {
            if (ledSender != null)
            {
                return await ledSender.GetAvailablePalettesAsync();
            }

            try
            {
                using var probeSender = new WledDdpNetworkSender(settings.EspIpAddress, settings.LedCount);
                return await probeSender.GetAvailablePalettesAsync();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        public async Task<List<WledEffectMetadata>> GetWledEffectMetadataAsync()
        {
            if (ledSender != null)
            {
                return await ledSender.GetEffectMetadataAsync();
            }

            try
            {
                using var probeSender = new WledDdpNetworkSender(settings.EspIpAddress, settings.LedCount);
                return await probeSender.GetEffectMetadataAsync();
            }
            catch (Exception)
            {
                return new List<WledEffectMetadata>();
            }
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
                StopCapture();

                profileWatcher?.Dispose();
                profileWatcher = null;

                stateWatcher?.Dispose();
                stateWatcher = null;

                ledSender?.Close();
                ledSender?.Dispose();
                ledSender = null;

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