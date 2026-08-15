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
                    pipelineManager?.ExitAmbientMode();
                    isAmbientModeActive = false;

                    // NOWOŚĆ: wymuszamy natychmiastową, świeżą ocenę aktywnego procesu
                    // po wyjściu z trybu ambientowego, zamiast czekać na przypadkową
                    // zmianę fokusu okna - to zapewnia powrót do profilu odpowiadającego
                    // faktycznie aktywnej aplikacji w momencie powrotu z idle/blokady,
                    // a nie do stanu, który mógł "wisieć" z czasu przed wejściem w ambient.
                    profileWatcher?.ResetActiveProfile();

                    // NOWOŚĆ: przywraca tryb wyświetlania WLED zapisany przy wejściu w ambient,
                    // niezależnie od tego, czy pipelineManager istnieje - jeśli Video Sync nigdy
                    // nie było uruchomione (czysty tryb WLED Effects/Static Color), powyższe
                    // ExitAmbientMode() na pipelineManager jest no-opem (pipelineManager == null),
                    // więc to JEDYNA ścieżka realnie przywracająca efekt na urządzeniu ESP.
                    _ = RestorePreviousDisplayModeAsync();

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
            if (profile == null || currentZones == null || pipelineManager == null)
            {
                return;
            }

            // NOWOŚĆ: profile mogą teraz wymuszać stały kolor albo efekt WLED zamiast
            // zwykłej zmiany parametrów obrazu Video Sync (np. "gdy uruchamiam TeamSpeak,
            // zaświeć diody na stały niebieski"). Rozwidlamy logikę na samym wejściu -
            // te dwa tryby nie dotykają imageProcessor/pipelineManager w ogóle, tylko
            // wysyłają komendę do WLED przez już istniejące ActivateStaticColorAsync /
            // ActivateWledEffectAsync i aktualizują CurrentProfileName/ProfileChanged
            // identycznie jak zwykły profil DSP.
            if (profile.ActionType == ProfileActionType.StaticColor)
            {
                _ = ActivateStaticColorAsync(profile.StaticColorR, profile.StaticColorG, profile.StaticColorB);
                pipelineManager.NotifyDisplayModeChanged();

                CurrentProfileName = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? "Domyślny"
                    : profile.DisplayName;

                currentProfileId = profile.ProfileId;
                SetStatus(EngineStatusInfo.Running($"Profil aktywny: {CurrentProfileName} (stały kolor, źródło: {triggerSource})"));
                ProfileChanged?.Invoke(CurrentProfileName);
                return;
            }

            if (profile.ActionType == ProfileActionType.WledEffect)
            {
                _ = ActivateWledEffectAsync(
                    profile.WledEffectId,
                    profile.WledEffectSpeed,
                    profile.WledEffectIntensity,
                    profile.WledPaletteId,
                    (profile.WledPrimaryColorR, profile.WledPrimaryColorG, profile.WledPrimaryColorB),
                    (profile.WledSecondaryColorR, profile.WledSecondaryColorG, profile.WledSecondaryColorB),
                    profile.WledEffectBrightness);
                pipelineManager.NotifyDisplayModeChanged();

                CurrentProfileName = string.IsNullOrWhiteSpace(profile.DisplayName)
                    ? "Domyślny"
                    : profile.DisplayName;

                currentProfileId = profile.ProfileId;
                SetStatus(EngineStatusInfo.Running($"Profil aktywny: {CurrentProfileName} (efekt WLED, źródło: {triggerSource})"));
                ProfileChanged?.Invoke(CurrentProfileName);
                return;
            }
            
            // NOWOŚĆ / FIX: profil typu ImageDsp musi jawnie przywrócić Video Sync, jeśli
            // poprzedni aktywny profil ustawił StaticColor/WledEffect - inaczej ConsumerLoopAsync
            // w PipelineManager nadal ignorował imageProcessor (bo settings.ActiveDisplayMode
            // zostawał na StaticColor/WledEffect), co wymagało ręcznej zmiany trybu w UI silnika.
            if (settings.ActiveDisplayMode != DisplayMode.VideoSync)
            {
                settings.ActiveDisplayMode = DisplayMode.VideoSync;
                pipelineManager.NotifyDisplayModeChanged();
            }

            var newImageProcessor = new ImageProcessor(currentZones);

            if (imageProcessor != null)
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

            CurrentProfileName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "Domyślny"
                : profile.DisplayName;

            currentProfileId = profile.ProfileId;

            SetStatus(EngineStatusInfo.Running(
                $"Profil aktywny: {CurrentProfileName} (źródło: {triggerSource})"));

            ProfileChanged?.Invoke(CurrentProfileName);
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
            profileWatcher?.SetProfiles(settings.Profiles);

            if (IsCapturing && !isProfilePreviewActive)
            {
                ActivateDefaultProfile("odświeżenie listy profili");
            }
        }

        private void OnProfileActivationRequested(object? sender, ProfileActivatedEventArgs e)
        {
            if (isProfilePreviewActive)
            {
                Debug.WriteLine(
                    "[DIAG] AppEngineHost: automatyczna aktywacja profilu pominięta — trwa podgląd na żywo.");

                return;
            }

            // NOWOŚĆ: blokuje automatyczne przełączanie profili w trakcie trybu
            // ambientowego (blokada ekranu / bezczynność) - ProcessProfileWatcher działa
            // niezależnie na własnym timerze i bez tej ochrony mógł nadpisać imageProcessor
            // mimo że PipelineManager renderuje w tym czasie efekt ambientowy, co po powrocie
            // z idle prowadziło do przywrócenia złego profilu.
            if (isAmbientModeActive)
            {
                Debug.WriteLine(
                    "[DIAG] AppEngineHost: automatyczna aktywacja profilu pominięta — trwa tryb ambientowy.");

                return;
            }

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
                // Video Sync jest już obsłużony przez pipelineManager.ExitAmbientMode(),
                // jeśli przechwytywanie było aktywne w momencie wejścia w ambient.
                break;
        }

        Debug.WriteLine($"[DIAG] AppEngineHost: przywrócono tryb '{snapshot.Mode}' po wybudzeniu/odblokowaniu.");
        return true;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[DIAG] AppEngineHost: błąd podczas przywracania trybu po wybudzeniu: {ex.Message}");
        return false;
    }
    finally
    {
        preAmbientSnapshot = null;
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

            return await ledSender.SetEffectAsync(
                fxId, speed, intensity, paletteId, primaryColor, secondaryColor, brightness,
                custom1, custom2, custom3, check1, check2, check3, cancellationToken);
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
        public async Task<bool> DisableWledRealtimeOverrideAsync(CancellationToken cancellationToken = default)
        {
            if (ledSender == null) return false;
            return await ledSender.DisableRealtimeOverrideAsync(cancellationToken);
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

        public void Stop()
        {
            try
            {
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