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
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT.Interop;

namespace AmbilightEngine
{
    public sealed class AppEngineHost : IDisposable
    {
        private readonly struct AmbientDisplaySnapshot
        {
            public AmbientDisplaySnapshot(
                DisplayMode mode,
                int wledEffectId,
                int wledPaletteId,
                int wledSpeed,
                int wledIntensity,
                int wledBrightness,
                (byte R, byte G, byte B) wledPrimaryColor,
                (byte R, byte G, byte B) wledSecondaryColor)
            {
                Mode = mode;
                WledEffectId = wledEffectId;
                WledPaletteId = wledPaletteId;
                WledSpeed = wledSpeed;
                WledIntensity = wledIntensity;
                WledBrightness = wledBrightness;
                WledPrimaryColor = wledPrimaryColor;
                WledSecondaryColor = wledSecondaryColor;
            }

            public DisplayMode Mode { get; }

            public int WledEffectId { get; }

            public int WledPaletteId { get; }

            public int WledSpeed { get; }

            public int WledIntensity { get; }

            public int WledBrightness { get; }

            public (byte R, byte G, byte B) WledPrimaryColor { get; }

            public (byte R, byte G, byte B) WledSecondaryColor { get; }
        }

        private readonly AmbilightSettings settings;

        private WgcCaptureEngine? captureEngine;
        private ImageProcessor? imageProcessor;
        private WledDdpNetworkSender? ledSender;
        private PipelineManager? pipelineManager;
        private SystemStateWatcher? stateWatcher;
        private ProcessProfileWatcher? profileWatcher;
        private volatile bool isProfilePreviewActive;
        private int videoSyncTransitionInProgress;
        private readonly object whitePresetTransitionLock = new();
        private CancellationTokenSource? whitePresetTransitionCts;
        private float currentWhitePresetTransitionKelvin = 6500f;

        private static readonly int[] WhitePresetKelvinCycle =
        {
    2700,
    4000,
    5000,
    6500,
    9300
};

        private const int WhitePresetTransitionDurationMs = 450;
        private const int WhitePresetTransitionFrameIntervalMs = 25;
        // NOWOŚĆ: chroni automatyczne przełączanie profili (ProcessProfileWatcher) przed
        // nadpisaniem imageProcessor w trakcie trwania trybu ambientowego (blokada/idle).
        // Bez tej flagi watcher (działający na własnym timerze 500ms, niezależnie od
        // stanu ambientu) mógł w trakcie idle wykryć zmianę aktywnego procesu i wywołać
        // ActivateProfile, które nadpisywało imageProcessor mimo że PipelineManager był
        // w trybie ambientowym - po powrocie z idle użytkownik widział losowy profil
        // zamiast tego, który był aktywny przed wejściem w ambient.
        private volatile bool isAmbientModeActive;
        private AmbientDisplaySnapshot? preAmbientSnapshot;
        private int lastCapturedWidth;
        private int lastCapturedHeight;
        private CaptureZone[]? currentZones;

        private readonly BlackBarDetectionService blackBarDetector = new BlackBarDetectionService();
        private BlackBarInsets currentInsets = BlackBarInsets.None;

        public bool IsRunning { get; private set; }
        public bool IsCapturing { get; private set; }
        public int MasterBrightnessPercent => settings.MasterBrightnessPercent;
        public string CurrentProfileName { get; private set; } = "Domyślny";
        private string? currentProfileId;
        public EngineStatusInfo CurrentStatus { get; private set; } = EngineStatusInfo.Stopped("Gotowy do startu.");

        public event Action<EngineStatusInfo>? StatusChanged;
        public event Action<string>? ProfileChanged;

        public AppEngineHost(AmbilightSettings settings)
        {
            this.settings = settings;
            blackBarDetector.IsEnabled = settings.EnableBlackBarDetection;
        }

        // NOWOŚĆ / FIX: jedno, wspólne miejsce konfigurujące dynamikę (Attack, Decay,
        // Czułość, MinBrightness) i próbkowanie (PixelSkipStep, peak-blend, shadow boost,
        // noise floor) na podstawie AmbilightSettings. Musi być wywołane dla KAŻDEGO nowo
        // utworzonego ImageProcessor - wcześniej ActivateProfile (wywoływane przy każdej
        // zmianie aktywnego okna) w ogóle nie wołało SetDynamics/SetQuality, więc czułość
        // i pokrewne parametry cichcem resetowały się do wartości domyślnych klasy (1.0 / 0)
        // przy każdym przełączeniu profilu - niezależnie od tego, co użytkownik ustawił
        // w Ustawieniach obrazu. Wywołuj PRZED ApplyDspParameters/ApplyColorCalibration,
        // ponieważ ApplyDspParameters wewnętrznie przekazuje bieżące pola sensitivity/
        // minBrightness dalej do SetDynamics - taka kolejność zachowuje wartości globalne,
        // a jednocześnie pozwala profilowi nadpisać tylko Attack/Decay (przez Smoothing).
        private void ConfigureProcessorDynamics(ImageProcessor processor)
        {
            processor.SetDynamics(
                (float)settings.MotionAttackSpeed,
                (float)settings.MotionDecaySpeed,
                (float)settings.ColorSensitivity,
                settings.MinimumBrightnessFloor);

            processor.SetQuality(settings.PixelSkipStep);

            processor.SetAdvancedSampling(
                (float)settings.ZonePeakWeight,
                (float)settings.ShadowBoostStrength,
                settings.NoiseFloor,
                settings.EdgeFeatherPixels,
                (float)settings.PhaseSmoothingStrength,
                (float)settings.ChannelGainR,
                (float)settings.ChannelGainG,
                (float)settings.ChannelGainB);

            // NOWOŚĆ: pełna kalibracja per-kanał RGB (Gain + Gamma + Offset), sterowana
            // przez CalibrationOverlayWindow (9 sliderów widocznych od razu).
            processor.SetChannelCalibration(
                (float)settings.ChannelGainR, (float)settings.ChannelGammaR, (float)settings.ChannelOffsetR,
                (float)settings.ChannelGainG, (float)settings.ChannelGammaG, (float)settings.ChannelOffsetG,
                (float)settings.ChannelGainB, (float)settings.ChannelGammaB, (float)settings.ChannelOffsetB);
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
                ConfigureProcessorDynamics(imageProcessor);
                ApplyColorCalibrationToProcessor(imageProcessor);

                ledSender = new WledDdpNetworkSender(settings.EspIpAddress, zones.Length);
                ledSender.Open();

                stateWatcher?.Dispose();
                stateWatcher = new SystemStateWatcher(settings, windowHandle);
                stateWatcher.AmbientModeRequested += trigger =>
                {
                    isAmbientModeActive = true;
                    SaveAmbientDisplaySnapshot();

                    var config = trigger == SystemAmbientTrigger.LockOrSleep
                    ? settings.LockScreenAmbient
                    : settings.IdleAmbient;

                    pipelineManager?.EnterAmbientMode(config);
                    SetStatus(EngineStatusInfo.Ambient($"Tryb ambientowy: {trigger} (efekt WLED #{config.EffectId}, włączony: {config.IsEnabled})"));
                };
                stateWatcher.NormalModeRequested += () =>
                {
                    _ = RestoreAfterSystemWakeAsync();
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
                AppCapabilityAccessStatus borderlessAccess =
                    await GraphicsCaptureAccess.RequestAccessAsync(
                        GraphicsCaptureAccessKind.Borderless);

                bool canDisableCaptureBorder =
                    borderlessAccess == AppCapabilityAccessStatus.Allowed;

                Debug.WriteLine(
                    $"[DIAG] Borderless Capture: wynik zgody Windows = {borderlessAccess}.");

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
                ConfigureProcessorDynamics(newProcessor);
                ApplyProfileOrCalibration(newProcessor);
                imageProcessor = newProcessor;

                if (zones.Length != ledSender!.LedCount)
                {
                    ledSender.Reconfigure(zones.Length);
                }

                captureEngine?.Dispose();

                captureEngine = new WgcCaptureEngine
                {
                    IsBorderRequired = !canDisableCaptureBorder
                };

                pipelineManager?.Dispose();
                pipelineManager = new PipelineManager(captureEngine, imageProcessor, ledSender, settings, zones.Length);
                pipelineManager.SetBlackBarDetectionEnabled(settings.EnableBlackBarDetection);
                pipelineManager.BlackBarInsetsChanged += OnBlackBarInsetsChanged;
                pipelineManager.Start();

                captureEngine.Start(item);

                IsCapturing = true;

                profileWatcher?.ResetActiveProfile();

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
            if (profile is null)
            {
                return;
            }

            if (!IsRunning || ledSender is null)
            {
                Debug.WriteLine(
                    $"[DIAG] ActivateProfile pominięte dla '{profile.DisplayName}': brak połączenia z WLED.");

                return;
            }

            string profileName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "Domyślny"
                : profile.DisplayName;

            try
            {
                if (profile.ActionType == ProfileActionType.StaticColor)
                {
                    _ = ApplyStaticColorAsync(
    profile.StaticColorR,
    profile.StaticColorG,
    profile.StaticColorB);

                    CurrentProfileName = profileName;
                    currentProfileId = profile.ProfileId;

                    SetStatus(EngineStatusInfo.Running(
                        $"Profil aktywny: {profileName} — stały kolor, źródło: {triggerSource}."));

                    ProfileChanged?.Invoke(CurrentProfileName);

                    Debug.WriteLine(
                        $"[DIAG] Aktywowano profil StaticColor '{profileName}', źródło: {triggerSource}.");

                    return;
                }

                if (profile.ActionType == ProfileActionType.WledEffect)
                {
                    _ = ActivateWledEffectAsync(
                        profile.WledEffectId,
                        profile.WledEffectSpeed,
                        profile.WledEffectIntensity,
                        profile.WledPaletteId,
                        (profile.WledPrimaryColorR,
                         profile.WledPrimaryColorG,
                         profile.WledPrimaryColorB),
                        (profile.WledSecondaryColorR,
                         profile.WledSecondaryColorG,
                         profile.WledSecondaryColorB),
                        profile.WledEffectBrightness);

                    pipelineManager?.NotifyDisplayModeChanged();

                    CurrentProfileName = profileName;
                    currentProfileId = profile.ProfileId;

                    SetStatus(EngineStatusInfo.Running(
                        $"Profil aktywny: {profileName} — efekt WLED, źródło: {triggerSource}."));

                    ProfileChanged?.Invoke(CurrentProfileName);

                    Debug.WriteLine(
                        $"[DIAG] Aktywowano profil WledEffect '{profileName}', źródło: {triggerSource}.");

                    return;
                }

                // ImageDsp: przywraca analizę obrazu tylko w już działającej sesji Video Sync.
                // Nie uruchamiamy tu StartCaptureAsync(), bo automatyczne profile nie mogą
                // wywoływać systemowego selektora monitora.
                if (!IsCapturing || currentZones is null || pipelineManager is null)
                {
                    CurrentProfileName = profileName;
                    currentProfileId = profile.ProfileId;

                    SetStatus(EngineStatusInfo.Running(
                        $"Profil „{profileName}” oczekuje na uruchomienie Video Sync."));

                    ProfileChanged?.Invoke(CurrentProfileName);

                    Debug.WriteLine(
                        $"[DIAG] Profil ImageDsp '{profileName}' oczekuje: Video Sync nie działa.");

                    return;
                }

                if (settings.ActiveDisplayMode != DisplayMode.VideoSync)
                {
                    _ = RestoreVideoSyncAfterProfileAsync(
                        pipelineManager,
                        profileName,
                        transitionToVideoSync: true);

                    Debug.WriteLine(
                        $"[DIAG] Profil ImageDsp '{profileName}' przywraca Video Sync.");
                }

                var newImageProcessor = new ImageProcessor(currentZones);

                if (imageProcessor is not null)
                {
                    newImageProcessor.SeedState(imageProcessor);
                }
                ConfigureProcessorDynamics(newImageProcessor);

                newImageProcessor.ApplyDspParameters(
                    profile.BrightnessPercent,
                    profile.SaturationBoost,
                    profile.SmoothingSpeedMs,
                    profile.BlackCutoffThreshold,
                    profile.ColorTemperatureKelvin,
                    profile.GammaValue);

                newImageProcessor.SetWallColorFromHex(
                    settings.WallColorHex,
                    settings.WallColorStrength);

                pipelineManager.ReplaceImageProcessor(newImageProcessor);
                imageProcessor = newImageProcessor;

                CurrentProfileName = profileName;
                currentProfileId = profile.ProfileId;

                SetStatus(EngineStatusInfo.Running(
                    $"Profil aktywny: {profileName}, źródło: {triggerSource}."));

                ProfileChanged?.Invoke(CurrentProfileName);

                Debug.WriteLine(
                    $"[DIAG] Aktywowano profil ImageDsp '{profileName}', źródło: {triggerSource}.");
            }
            catch (Exception ex)
            {
                SetStatus(EngineStatusInfo.Error(
                    $"Nie udało się zastosować profilu „{profileName}”: {ex.Message}"));

                Debug.WriteLine(
                    $"[DIAG] Błąd ActivateProfile dla '{profileName}': {ex}");
            }
        }

        public void PreviewProfile(AppProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            if (currentZones == null || pipelineManager == null)
            {
                SetStatus(EngineStatusInfo.Stopped(
                    "Podgląd profilu jest dostępny po uruchomieniu Video Sync."));

                return;
            }

            isProfilePreviewActive = true;

            ActivateProfile(profile, "podgląd na żywo");
        }

        public void EndProfilePreview()
        {
            if (!isProfilePreviewActive)
            {
                return;
            }

            isProfilePreviewActive = false;

            profileWatcher?.ResetActiveProfile();

            Debug.WriteLine(
                "[DIAG] AppEngineHost: zakończono podgląd profilu; przywracam automatyczny wybór profilu.");
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

                currentProfileId = defaultProfile.ProfileId;

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

                // FIX (ten sam mechanizm jak w OnBlackBarInsetsChanged) - zachowujemy
                // płynność EMA przy ręcznej zmianie geometrii, o ile liczba LED nie
                // zmieniła się (SeedState samo sprawdza zgodność długości tablic).
                newProcessor.SeedState(imageProcessor);

                ConfigureProcessorDynamics(newProcessor);
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
            if (!string.IsNullOrWhiteSpace(currentProfileId))
            {
                var activeProfile = settings.Profiles?.Find(p =>
                    string.Equals(p.ProfileId, currentProfileId, StringComparison.Ordinal));

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

        private CaptureZone[] BuildZones(
    int width,
    int height,
    BlackBarInsets insets = default)
        {
            int effectiveWidth = width - insets.Left - insets.Right;
            int effectiveHeight = height - insets.Top - insets.Bottom;

            if (effectiveWidth <= 0 || effectiveHeight <= 0)
            {
                insets = BlackBarInsets.None;
                effectiveWidth = width;
                effectiveHeight = height;
            }

            int effectiveSamplingDepth = Math.Min(
                settings.SamplingDepth,
                Math.Min(effectiveWidth, effectiveHeight));

            effectiveSamplingDepth = Math.Max(1, effectiveSamplingDepth);

            Debug.WriteLine(
                $"[DIAG] BuildZones: ekran={width}x{height}, " +
                $"insets={insets}, obszar={effectiveWidth}x{effectiveHeight}, " +
                $"offsetX={insets.Left}, offsetY={insets.Top}");

            if (settings.UseCustomZoneLayout)
            {
                return ZoneMapGenerator.Generate(
                    effectiveWidth,
                    effectiveHeight,
                    settings.TopLedCount,
                    settings.BottomLedCount,
                    settings.LeftLedCount,
                    settings.RightLedCount,
                    effectiveSamplingDepth,
                    settings.ZoneStartCorner,
                    settings.ZoneStripDirection,
                    settings.ZoneShiftOffset,
                    settings.ExcludedLedIndices,
                    offsetX: insets.Left,
                    offsetY: insets.Top);
            }

            return ZoneMapGenerator.Generate(
                effectiveWidth,
                effectiveHeight,
                settings.LedCount,
                effectiveSamplingDepth,
                settings.ZoneStartCorner,
                settings.ZoneStripDirection,
                offsetX: insets.Left,
                offsetY: insets.Top);
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

                // FIX (migotanie): bez tego wywołania nowy processor startował z EMA
                // wyzerowanym do czerni (previousR/G/B = 0) przy KAŻDEJ zmianie wykrytych
                // insetów - nawet fałszywej, spowodowanej przejściową anomalią w detekcji
                // black barów - co dawało realny, widoczny skok do czerni i płynny powrót
                // (efekt "mrygnięcia"). SeedState kopiuje stan wygładzania ze starego
                // processora, więc zmiana geometrii stref nie przerywa płynności światła.
                newProcessor.SeedState(imageProcessor);

                ConfigureProcessorDynamics(newProcessor);
                ApplyProfileOrCalibration(newProcessor);

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
        public void SetBlackBarDetectionParameters(byte threshold, double minRatio)
        {
            blackBarDetector.BlackThreshold = threshold;
            blackBarDetector.MinBlackRatio = minRatio;
            pipelineManager?.SetBlackBarDetectionParameters(threshold, minRatio);
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
            try
            {
                // ProcessProfileWatcher dostaje DefaultProfile w konstruktorze.
                // Samo SetProfiles() odświeża tylko profile przypisane do aplikacji,
                // ale nie aktualizuje fallbackProfile. Tworzymy watcher od nowa,
                // aby przejął aktualny settings.DefaultProfile.
                profileWatcher?.Dispose();
                profileWatcher = null;

                InitializeProfileWatcher();

                // Nie wymuszamy tu od razu profilu domyślnego na WLED.
                // Watcher oceni aktywne okno po swoim cyklu 500 ms + debounce.
                profileWatcher?.ResetActiveProfile();

                Debug.WriteLine(
                    "[DIAG] AppEngineHost: odświeżono profile oraz domyślny profil watchera.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] AppEngineHost: błąd odświeżania profili: {ex}");
            }
        }

        private void OnProfileActivationRequested(
    object? sender,
    ProfileActivatedEventArgs e)
        {
            if (e?.Profile is null)
            {
                return;
            }

            if (isProfilePreviewActive)
            {
                Debug.WriteLine(
                    "[DIAG] Automatyczna zmiana profilu pominięta: trwa podgląd na żywo.");

                return;
            }

            if (isAmbientModeActive)
            {
                Debug.WriteLine(
                    "[DIAG] Automatyczna zmiana profilu pominięta: trwa tryb ambientowy.");

                return;
            }
           
            if (!IsRunning || ledSender is null)
            {
                Debug.WriteLine(
                    $"[DIAG] Automatyczna zmiana profilu '{e.Profile.DisplayName}' pominięta: brak WLED.");

                return;
            }

            ActivateProfile(e.Profile, e.TriggeringProcessName);
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
        private void SaveAmbientDisplaySnapshot()
{
    preAmbientSnapshot = new AmbientDisplaySnapshot(
        settings.ActiveDisplayMode,
        settings.LastWledEffectId,
        settings.LastWledPaletteId,
        settings.LastWledSpeed,
        settings.LastWledIntensity,
        settings.LastWledBrightness,
        (settings.LastWledPrimaryColorR, settings.LastWledPrimaryColorG, settings.LastWledPrimaryColorB),
        (settings.LastWledSecondaryColorR, settings.LastWledSecondaryColorG, settings.LastWledSecondaryColorB));
}

        public async Task<bool> RestorePreviousDisplayModeAsync()
        {
            if (preAmbientSnapshot is not AmbientDisplaySnapshot snapshot)
            {
                Debug.WriteLine(
                    "[DIAG] AppEngineHost: brak snapshotu trybu sprzed uśpienia/ambientu.");

                return false;
            }

            try
            {
                switch (snapshot.Mode)
                {
                    case DisplayMode.StaticColor:
                        await ActivateStaticColorAsync(
                            snapshot.WledPrimaryColor.R,
                            snapshot.WledPrimaryColor.G,
                            snapshot.WledPrimaryColor.B);

                        break;

                    case DisplayMode.WledEffects:
                        await ActivateWledEffectAsync(
                            snapshot.WledEffectId,
                            snapshot.WledSpeed,
                            snapshot.WledIntensity,
                            snapshot.WledPaletteId,
                            snapshot.WledPrimaryColor,
                            snapshot.WledSecondaryColor,
                            snapshot.WledBrightness);

                        break;

                    case DisplayMode.VideoSync:
                    default:
                        if (!IsCapturing || pipelineManager is null)
                        {
                            Debug.WriteLine(
                                "[DIAG] Resume: Video Sync nie może zostać przywrócony, " +
                                "ponieważ capture lub pipeline nie są aktywne.");

                            return false;
                        }

                        bool restored = await RestoreVideoSyncAfterWakeWithRetryAsync();

                        if (!restored)
                        {
                            return false;
                        }

                        break;
                }

                Debug.WriteLine(
                    $"[DIAG] AppEngineHost: przywrócono tryb '{snapshot.Mode}' po wybudzeniu/odblokowaniu.");

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] AppEngineHost: błąd podczas przywracania trybu po wybudzeniu: {ex}");

                return false;
            }
            finally
            {
                preAmbientSnapshot = null;
            }
        }
        private async Task RestoreAfterSystemWakeAsync()
        {
            try
            {
                // 1. Odblokowuje przetwarzanie ramek WGC, ale nie steruje WLED.
                pipelineManager?.ExitAmbientMode();

                // 2. To jedyna ścieżka przywracająca stan WLED i tryb.
                bool restored = await RestorePreviousDisplayModeAsync();

                if (!restored)
                {
                    Debug.WriteLine(
                        "[DIAG] AppEngineHost: nie przywrócono trybu po wybudzeniu/odblokowaniu.");

                    return;
                }

                // 3. Dopiero po końcu restore dopuszczamy watcher profili.
                isAmbientModeActive = false;

                if (IsCapturing && settings.ActiveDisplayMode == DisplayMode.VideoSync)
                {
                    profileWatcher?.ResetActiveProfile();
                }

                SetStatus(IsCapturing
                    ? EngineStatusInfo.Running("Przechwytywanie aktywne.")
                    : EngineStatusInfo.Running("Połączono z WLED, przechwytywanie wyłączone."));

                Debug.WriteLine(
                    "[DIAG] AppEngineHost: zakończono przywracanie po wybudzeniu/odblokowaniu.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] AppEngineHost: błąd odtwarzania po wybudzeniu/odblokowaniu: {ex}");
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
            await ledSender.DisableRealtimeOverrideAsync(cancellationToken);

            int baseBrightness = brightness ?? settings.LastWledBrightness;

            int effectiveBrightness = ScaleWledBrightness(
                baseBrightness,
                settings.MasterBrightnessPercent);

            return await ledSender.SetEffectAsync(
                fxId,
                speed,
                intensity,
                paletteId,
                primaryColor,
                secondaryColor,
                effectiveBrightness,
                custom1,
                custom2,
                custom3,
                check1,
                check2,
                check3,
                cancellationToken);
        }
        public async Task<bool> ActivateStaticColorAsync(
    byte red,
    byte green,
    byte blue,
    CancellationToken cancellationToken = default)
        {
            if (ledSender == null)
            {
                return false;
            }

            settings.ActiveDisplayMode = DisplayMode.StaticColor;
            settings.StaticColorR = red;
            settings.StaticColorG = green;
            settings.StaticColorB = blue;

            return await ledSender.SetEffectAsync(
                fxId: 0,
                speed: 0,
                intensity: 0,
                paletteId: 0,
                primaryColor: (red, green, blue),
                secondaryColor: (0, 0, 0),
                brightness: 255,
                cancellationToken: cancellationToken);
        }
        public async Task<bool> ApplyStaticColorWithTransitionAsync(
    byte red,
    byte green,
    byte blue,
    CancellationToken cancellationToken = default)
        {
            settings.StaticColorR = red;
            settings.StaticColorG = green;
            settings.StaticColorB = blue;

            bool realtimeOverrideDisabled =
                await DisableWledRealtimeOverrideAsync(cancellationToken);

            if (!realtimeOverrideDisabled)
            {
                Debug.WriteLine(
                    "[DIAG] Static Color: nie udało się wyłączyć realtime override.");
            }
            
            if (pipelineManager is not null && IsCapturing)
            {
                pipelineManager.TransitionToStaticColor(red, green, blue);

                Debug.WriteLine(
                    $"[DIAG] Static Color transition: RGB({red}, {green}, {blue}).");

                return true;
            }

            return await ActivateStaticColorAsync(red, green, blue, cancellationToken);
        }

        public async Task<bool> ApplyVideoSyncWithTransitionAsync(
    CancellationToken cancellationToken = default)
        {
            if (!IsCapturing || pipelineManager is null)
            {
                SetStatus(EngineStatusInfo.Running(
                    "Video Sync wymaga aktywnego przechwytywania ekranu."));

                return false;
            }

            if (Interlocked.Exchange(ref videoSyncTransitionInProgress, 1) != 0)
            {
                Debug.WriteLine(
                    "[DIAG] Video Sync: pominięto zduplikowane żądanie z interfejsu.");

                return true;
            }

            try
            {
                bool overrideDisabled =
                    await DisableWledRealtimeOverrideAsync(cancellationToken);

                if (!overrideDisabled)
                {
                    Debug.WriteLine(
                        "[DIAG] Video Sync: nie udało się wyłączyć realtime override.");
                }

                pipelineManager.TransitionToVideoSync();

                return true;
            }
            finally
            {
                Volatile.Write(ref videoSyncTransitionInProgress, 0);
            }
        }
        public Task<bool> ApplyStaticColorAsync(
    byte red,
    byte green,
    byte blue,
    CancellationToken cancellationToken = default)
        {
            settings.ActiveDisplayMode = DisplayMode.StaticColor;
            settings.StaticColorR = red;
            settings.StaticColorG = green;
            settings.StaticColorB = blue;

            if (pipelineManager is not null && IsCapturing)
            {
                pipelineManager.TransitionToStaticColor(red, green, blue);

                Debug.WriteLine(
                    $"[DIAG] Static Color: przejście pipeline do RGB({red}, {green}, {blue}).");

                return Task.FromResult(true);
            }

            return ActivateStaticColorAsync(red, green, blue, cancellationToken);
        }
        public Task<bool> SetStaticColorWithTransitionAsync(
    byte red,
    byte green,
    byte blue,
    CancellationToken cancellationToken = default)
        {
            if (pipelineManager is not null && IsCapturing)
            {
                settings.ActiveDisplayMode = DisplayMode.StaticColor;
                settings.StaticColorR = red;
                settings.StaticColorG = green;
                settings.StaticColorB = blue;

                pipelineManager.TransitionToStaticColor(red, green, blue);

                Debug.WriteLine(
                    $"[DIAG] Static Color: płynne przejście do RGB({red}, {green}, {blue}).");

                return Task.FromResult(true);
            }

            return ActivateStaticColorAsync(red, green, blue, cancellationToken);
        }
        public async Task<bool> DisableWledRealtimeOverrideAsync(CancellationToken cancellationToken = default)
        {
            if (ledSender == null) return false;
            return await ledSender.DisableRealtimeOverrideAsync(cancellationToken);
        }
        private async Task RestoreVideoSyncAfterProfileAsync(
    PipelineManager pipeline,
    string profileName,
    bool transitionToVideoSync = false)
        {
            try
            {
                bool realtimeOverrideDisabled =
                    await DisableWledRealtimeOverrideAsync();

                if (!realtimeOverrideDisabled)
                {
                    Debug.WriteLine(
                        $"[DIAG] Nie udało się wyłączyć realtime override przed powrotem do Video Sync: {profileName}.");
                }

                settings.ActiveDisplayMode = DisplayMode.VideoSync;

                if (transitionToVideoSync)
                {
                    pipeline.TransitionToVideoSync();
                }
                else
                {
                    pipeline.NotifyDisplayModeChanged();
                }

                Debug.WriteLine(
                    $"[DIAG] Video Sync przywrócony po profilu '{profileName}', " +
                    $"realtime override wyłączony={realtimeOverrideDisabled}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] Błąd przywracania Video Sync po profilu '{profileName}': {ex}");
            }
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

        // FIX: teraz wywołuje ConfigureProcessorDynamics, żeby nowe parametry (peak-blend,
        // shadow boost, noise floor) trafiały do żywego procesora tak samo jak Attack/Decay/
        // Czułość/MinBrightness - jedna, spójna ścieżka konfiguracji.
        public void ApplyLiveSettings()
        {
            if (imageProcessor == null)
            {
                return;
            }

            ConfigureProcessorDynamics(imageProcessor);
        }

        // NOWOŚĆ: synchronizuje sesję "live" WLED w PipelineManagerze z aktualnie wybranym trybem
        // wyświetlania (Video Sync / Static Color / WLED Effects). Bezpieczne wywołanie nawet
        // gdy przechwytywanie nie jest aktywne - PipelineManager wtedy nic nie robi.
        public void NotifyDisplayModeChanged()
        {
            pipelineManager?.NotifyDisplayModeChanged();
        }
        private void SetStatus(EngineStatusInfo status)
        {
            CurrentStatus = status;
            StatusChanged?.Invoke(status);
        }
        private async Task<bool> RestoreVideoSyncAfterWakeWithRetryAsync()
        {
            const int maxAttempts = 6;
            TimeSpan retryDelay = TimeSpan.FromSeconds(1);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Debug.WriteLine(
                        $"[DIAG] Resume: próba {attempt}/{maxAttempts} przywrócenia Video Sync.");

                    bool restored = await ApplyVideoSyncWithTransitionAsync();

                    if (restored)
                    {
                        Debug.WriteLine(
                            $"[DIAG] Resume: Video Sync przywrócony w próbie {attempt}/{maxAttempts}.");

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[DIAG] Resume: próba {attempt}/{maxAttempts} nieudana: {ex.Message}");
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(retryDelay);
                }
            }

            Debug.WriteLine(
                "[DIAG] Resume: nie udało się przywrócić Video Sync po wszystkich próbach.");

            return false;
        }
        /// <summary>
        /// Ustawia globalny końcowy mnożnik jasności 0–100%.
        /// Nie zmienia jasności profilu, statycznego koloru ani bazowej jasności efektu WLED.
        /// </summary>
        public async Task<bool> SetMasterBrightnessPercentAsync(
            int brightnessPercent,
            CancellationToken cancellationToken = default)
        {
            int normalizedBrightness = Math.Clamp(brightnessPercent, 0, 100);

            if (settings.MasterBrightnessPercent == normalizedBrightness)
            {
                return true;
            }

            settings.MasterBrightnessPercent = normalizedBrightness;

            try
            {
                switch (settings.ActiveDisplayMode)
                {
                    case DisplayMode.WledEffects:
                        if (ledSender is null)
                        {
                            return false;
                        }

                        int baseBrightness = settings.LastWledBrightness;
                        int effectiveBrightness = ScaleWledBrightness(
                            baseBrightness,
                            normalizedBrightness);

                        bool effectApplied = await ledSender.SetEffectAsync(
                            settings.LastWledEffectId,
                            settings.LastWledSpeed,
                            settings.LastWledIntensity,
                            settings.LastWledPaletteId,
                            (settings.LastWledPrimaryColorR,
                             settings.LastWledPrimaryColorG,
                             settings.LastWledPrimaryColorB),
                            (settings.LastWledSecondaryColorR,
                             settings.LastWledSecondaryColorG,
                             settings.LastWledSecondaryColorB),
                            effectiveBrightness,
                            settings.LastWledCustom1,
                            settings.LastWledCustom2,
                            settings.LastWledCustom3,
                            settings.LastWledCheck1,
                            settings.LastWledCheck2,
                            settings.LastWledCheck3,
                            cancellationToken);

                        if (!effectApplied)
                        {
                            Debug.WriteLine(
                                "[DIAG] Master Brightness: nie udało się zastosować jasności efektu WLED.");

                            return false;
                        }

                        break;

                    case DisplayMode.StaticColor:
                    case DisplayMode.VideoSync:
                        pipelineManager?.RefreshMasterBrightness();
                        break;
                }

                SetStatus(EngineStatusInfo.Running(
                    $"Jasność główna: {normalizedBrightness}%."));

                Debug.WriteLine(
                    $"[DIAG] Master Brightness: ustawiono {normalizedBrightness}%.");

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] Master Brightness: błąd ustawiania {normalizedBrightness}%: {ex}");

                SetStatus(EngineStatusInfo.Error(
                    $"Nie udało się ustawić jasności głównej: {ex.Message}"));

                return false;
            }
        }

        /// <summary>
        /// Zwiększa Master Brightness o 5%. Z 0% przechodzi od razu na 5%.
        /// </summary>
        public Task<bool> IncreaseMasterBrightnessAsync(
            CancellationToken cancellationToken = default)
        {
            int current = settings.MasterBrightnessPercent;

            int target = current <= 0
                ? 5
                : Math.Min(100, current + 5);

            return SetMasterBrightnessPercentAsync(target, cancellationToken);
        }

/// <summary>
/// Zmniejsza Master Brightness o 5%, ale skrót nigdy nie schodzi poniżej 1%.
/// Wartość 0% pozostaje dostępna tylko przez świadome ustawienie suwakiem.
/// </summary>
public Task<bool> DecreaseMasterBrightnessAsync(
    CancellationToken cancellationToken = default)
        {
            int current = settings.MasterBrightnessPercent;

            int target = current <= 1
                ? 1
                : Math.Max(1, current - 5);

            return SetMasterBrightnessPercentAsync(target, cancellationToken);
        }

        private static int ScaleWledBrightness(
            int baseBrightness,
            int masterBrightnessPercent)
        {
            int normalizedBase = Math.Clamp(baseBrightness, 0, 255);
            int normalizedMaster = Math.Clamp(masterBrightnessPercent, 0, 100);

            return (int)Math.Clamp(
                Math.Round(normalizedBase * normalizedMaster / 100.0),
                0,
                255);
        }
        public void Stop()
        {
            try
            {
                lock (whitePresetTransitionLock)
                {
                    whitePresetTransitionCts?.Cancel();
                    whitePresetTransitionCts?.Dispose();
                    whitePresetTransitionCts = null;
                }
                isProfilePreviewActive = false;
                isAmbientModeActive = false;

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
                currentProfileId = null;
                ProfileChanged?.Invoke(CurrentProfileName);

                SetStatus(EngineStatusInfo.Stopped("Ambilight został zatrzymany."));
            }
            catch (Exception ex)
            {
                IsRunning = false;
                SetStatus(EngineStatusInfo.Error(
                    $"Wystąpił błąd podczas zatrzymywania: {ex.Message}"));
            }
        }

        private AmbientDisplaySnapshot? preBlackoutSnapshot;
        private bool isBlackoutActive;

        /// <summary>
        /// Przełącza tryb wyświetlania w kolejności Video Sync -> Static Color -> WLED Effects -> Video Sync.
        /// Używane przez skrót globalny "mode.cycle". Reużywa istniejących ścieżek przejścia
        /// (ApplyVideoSyncWithTransitionAsync / SetStaticColorWithTransitionAsync / ActivateWledEffectAsync),
        /// żeby zachować tę samą płynność i logikę realtime override, co przy zmianie z UI.
        /// </summary>
        public async Task<bool> CycleDisplayModeAsync(CancellationToken cancellationToken = default)
        {
            if (!IsRunning)
            {
                Debug.WriteLine("[DIAG] CycleDisplayModeAsync: pominięto, brak połączenia z WLED.");
                return false;
            }

            try
            {
                switch (settings.ActiveDisplayMode)
                {
                    case DisplayMode.VideoSync:
                        bool switchedToStatic = await SetStaticColorWithTransitionAsync(
                            settings.StaticColorR,
                            settings.StaticColorG,
                            settings.StaticColorB,
                            cancellationToken);

                        Debug.WriteLine($"[DIAG] CycleDisplayModeAsync: VideoSync -> StaticColor ({switchedToStatic}).");
                        return switchedToStatic;

                    case DisplayMode.StaticColor:
                        var primaryColor = (settings.LastWledPrimaryColorR, settings.LastWledPrimaryColorG, settings.LastWledPrimaryColorB);
                        var secondaryColor = (settings.LastWledSecondaryColorR, settings.LastWledSecondaryColorG, settings.LastWledSecondaryColorB);

                        bool switchedToWled = await ActivateWledEffectAsync(
                            settings.LastWledEffectId,
                            settings.LastWledSpeed,
                            settings.LastWledIntensity,
                            settings.LastWledPaletteId,
                            primaryColor,
                            secondaryColor,
                            settings.LastWledBrightness,
                            settings.LastWledCustom1,
                            settings.LastWledCustom2,
                            settings.LastWledCustom3,
                            settings.LastWledCheck1,
                            settings.LastWledCheck2,
                            settings.LastWledCheck3,
                            cancellationToken);

                        pipelineManager?.NotifyDisplayModeChanged();

                        Debug.WriteLine($"[DIAG] CycleDisplayModeAsync: StaticColor -> WledEffects ({switchedToWled}).");
                        return switchedToWled;

                    case DisplayMode.WledEffects:
                    default:
                        if (!IsCapturing || pipelineManager is null)
                        {
                            Debug.WriteLine("[DIAG] CycleDisplayModeAsync: WledEffects -> VideoSync pominięte (capture nieaktywny).");
                            return false;
                        }

                        bool switchedToVideoSync = await ApplyVideoSyncWithTransitionAsync(cancellationToken);

                        Debug.WriteLine($"[DIAG] CycleDisplayModeAsync: WledEffects -> VideoSync ({switchedToVideoSync}).");
                        return switchedToVideoSync;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DIAG] CycleDisplayModeAsync: błąd przełączania trybu: {ex}");
                SetStatus(EngineStatusInfo.Error($"Nie udało się przełączyć trybu: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Przełącza (toggle) tymczasowe wygaszenie LED do czerni, zachowując pełny stan poprzedniego
        /// trybu (Video Sync / Static Color / WLED Effects wraz z parametrami). Drugie wywołanie tego
        /// samego skrótu przywraca dokładnie ten stan - reużywa mechanizmu snapshotu z trybu ambientowego
        /// (SaveAmbientDisplaySnapshot / RestorePreviousDisplayModeAsync), zamiast trwale nadpisywać
        /// ActiveDisplayMode, żeby użytkownik nie musiał ręcznie przywracać trybu po Blackout.
        /// </summary>
        public async Task<bool> ToggleBlackoutAsync(CancellationToken cancellationToken = default)
        {
            if (!IsRunning || ledSender is null)
            {
                Debug.WriteLine("[DIAG] ToggleBlackoutAsync: pominięto, brak połączenia z WLED.");
                return false;
            }

            try
            {
                if (isBlackoutActive)
                {
                    if (preBlackoutSnapshot is not AmbientDisplaySnapshot snapshot)
                    {
                        isBlackoutActive = false;
                        Debug.WriteLine("[DIAG] ToggleBlackoutAsync: brak snapshotu, nie mogę przywrócić stanu.");
                        return false;
                    }

                    switch (snapshot.Mode)
                    {
                        case DisplayMode.StaticColor:
                            await ActivateStaticColorAsync(
                                snapshot.WledPrimaryColor.R,
                                snapshot.WledPrimaryColor.G,
                                snapshot.WledPrimaryColor.B,
                                cancellationToken);
                            break;

                        case DisplayMode.WledEffects:
                            await ActivateWledEffectAsync(
                                snapshot.WledEffectId,
                                snapshot.WledSpeed,
                                snapshot.WledIntensity,
                                snapshot.WledPaletteId,
                                snapshot.WledPrimaryColor,
                                snapshot.WledSecondaryColor,
                                snapshot.WledBrightness,
                                cancellationToken: cancellationToken);

                            pipelineManager?.NotifyDisplayModeChanged();
                            break;

                        case DisplayMode.VideoSync:
                        default:
                            if (IsCapturing && pipelineManager is not null)
                            {
                                await ApplyVideoSyncWithTransitionAsync(cancellationToken);
                            }
                            break;
                    }

                    isBlackoutActive = false;
                    preBlackoutSnapshot = null;

                    SetStatus(EngineStatusInfo.Running("Blackout wyłączony, przywrócono poprzedni tryb."));
                    Debug.WriteLine("[DIAG] ToggleBlackoutAsync: przywrócono stan sprzed wygaszenia.");
                    return true;
                }

                preBlackoutSnapshot = new AmbientDisplaySnapshot(
                    settings.ActiveDisplayMode,
                    settings.LastWledEffectId,
                    settings.LastWledPaletteId,
                    settings.LastWledSpeed,
                    settings.LastWledIntensity,
                    settings.LastWledBrightness,
                    (settings.LastWledPrimaryColorR, settings.LastWledPrimaryColorG, settings.LastWledPrimaryColorB),
                    (settings.LastWledSecondaryColorR, settings.LastWledSecondaryColorG, settings.LastWledSecondaryColorB));

                if (pipelineManager is not null && IsCapturing)
                {
                    pipelineManager.TransitionToStaticColor(0, 0, 0);
                }
                else
                {
                    await ledSender.SetEffectAsync(
                        fxId: 0,
                        speed: 0,
                        intensity: 0,
                        paletteId: 0,
                        primaryColor: (0, 0, 0),
                        secondaryColor: (0, 0, 0),
                        brightness: 0,
                        cancellationToken: cancellationToken);
                }

                isBlackoutActive = true;

                SetStatus(EngineStatusInfo.Running("Blackout aktywny — światło wygaszone."));
                Debug.WriteLine("[DIAG] ToggleBlackoutAsync: wygaszono LED, zapisano snapshot poprzedniego stanu.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DIAG] ToggleBlackoutAsync: błąd: {ex}");
                SetStatus(EngineStatusInfo.Error($"Blackout nie powiódł się: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Przełącza temperaturę światła białego (ColorTemperatureKelvin) aktywnego profilu w ustalonym
        /// cyklu: 2700K -> 4000K -> 5000K -> 6500K -> 9300K -> z powrotem do 2700K. Modyfikuje profil
        /// wskazywany przez currentProfileId (albo DefaultProfile, jeśli żaden nie jest aktywny) i od razu
        /// re-aplikuje go przez ActivateProfile, żeby zmiana była widoczna na żywo na diodach LED.
        /// </summary>
        public int CycleWhitePreset()
        {
            try
            {
                AppProfile? targetProfile = null;

                if (!string.IsNullOrWhiteSpace(currentProfileId))
                {
                    targetProfile = settings.Profiles?.Find(profile =>
                        string.Equals(
                            profile.ProfileId,
                            currentProfileId,
                            StringComparison.Ordinal));
                }

                targetProfile ??= settings.DefaultProfile;

                if (targetProfile is null)
                {
                    Debug.WriteLine(
                        "[DIAG] CycleWhitePreset: brak aktywnego profilu do modyfikacji.");

                    return 0;
                }

                int currentIndex = Array.IndexOf(
                    WhitePresetKelvinCycle,
                    targetProfile.ColorTemperatureKelvin);

                int nextIndex = currentIndex >= 0
                    ? (currentIndex + 1) % WhitePresetKelvinCycle.Length
                    : 0;

                int targetKelvin = WhitePresetKelvinCycle[nextIndex];

                // Zapisujemy docelowy preset od razu, aby kolejny skrót poprawnie wybrał
                // następny punkt cyklu nawet wtedy, gdy bieżące przejście jeszcze trwa.
                targetProfile.ColorTemperatureKelvin = targetKelvin;

                StartWhitePresetTransition(targetKelvin);

                SetStatus(
                    EngineStatusInfo.Running(
                        $"Temperatura bieli: {targetKelvin}K."));

                Debug.WriteLine(
                    $"[DIAG] CycleWhitePreset: profil '{targetProfile.DisplayName}' " +
                    $"-> {targetKelvin}K (płynne przejście).");

                return targetKelvin;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DIAG] CycleWhitePreset: błąd: {ex}");

                return 0;
            }
        }
        private void StartWhitePresetTransition(int targetKelvin)
        {
            ImageProcessor? processor = imageProcessor;

            if (processor is null)
            {
                Debug.WriteLine(
                    "[DIAG] WhitePresetTransition: brak aktywnego ImageProcessor; " +
                    "preset zostanie zastosowany przy następnym uruchomieniu Video Sync.");

                currentWhitePresetTransitionKelvin = targetKelvin;
                return;
            }

            CancellationToken token;
            float startKelvin;

            lock (whitePresetTransitionLock)
            {
                whitePresetTransitionCts?.Cancel();
                whitePresetTransitionCts?.Dispose();

                whitePresetTransitionCts = new CancellationTokenSource();
                token = whitePresetTransitionCts.Token;

                // Odczytujemy faktyczną wartość z procesora, a nie tylko ustawienie profilu.
                // To pozwala płynnie przestawić kierunek nawet w środku poprzedniej animacji.
                startKelvin = processor.GetColorTemperatureKelvin();
                currentWhitePresetTransitionKelvin = startKelvin;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    const int frameCount =
                        WhitePresetTransitionDurationMs /
                        WhitePresetTransitionFrameIntervalMs;

                    for (int step = 1; step <= frameCount; step++)
                    {
                        token.ThrowIfCancellationRequested();

                        float linearProgress = step / (float)frameCount;

                        // Smoothstep: łagodne wejście i wyjście, bez mechanicznego skoku.
                        float progress = linearProgress * linearProgress *
                            (3f - 2f * linearProgress);

                        float interpolatedKelvin =
                            startKelvin +
                            (targetKelvin - startKelvin) * progress;

                        ImageProcessor? liveProcessor = imageProcessor;

                        if (liveProcessor is not null)
                        {
                            liveProcessor.SetColorTemperatureKelvin(
                                interpolatedKelvin);
                        }

                        currentWhitePresetTransitionKelvin =
                            interpolatedKelvin;

                        await Task.Delay(
                            WhitePresetTransitionFrameIntervalMs,
                            token).ConfigureAwait(false);
                    }

                    ImageProcessor? finalProcessor = imageProcessor;

                    if (finalProcessor is not null)
                    {
                        finalProcessor.SetColorTemperatureKelvin(targetKelvin);
                    }

                    currentWhitePresetTransitionKelvin = targetKelvin;

                    Debug.WriteLine(
                        $"[DIAG] WhitePresetTransition: zakończono przejście do {targetKelvin}K.");
                }
                catch (OperationCanceledException)
                {
                    // Użytkownik wybrał następny preset przed końcem poprzedniego przejścia.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[DIAG] WhitePresetTransition: błąd: {ex}");
                }
            }, CancellationToken.None);
        }
        public void Dispose()
        {
            Stop();
        }

       
    }

}