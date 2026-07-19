using System;
using Windows.Graphics.Capture;
using WinRT.Interop;
using AmbilightEngine.Core.Capture;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Pipeline;
using AmbilightEngine.Core.SystemState;

namespace AmbilightEngine
{
    // Centralny "silnik" aplikacji - żywy tak długo jak trwa cała sesja programu.
    // Strony UI (Dashboard, Settings) komunikują się z nim, nigdy nie tworząc własnych kopii Capture/Pipeline.
    public sealed class AppEngineHost : IDisposable
    {
        private readonly AmbilightSettings settings;

        private WgcCaptureEngine? captureEngine;
        private ImageProcessor? imageProcessor;
        private WledDdpNetworkSender? ledSender;
        private PipelineManager? pipelineManager;
        private SystemStateWatcher? stateWatcher;

        public bool IsRunning { get; private set; }
        public event Action<string>? StatusChanged;

        public AppEngineHost(AmbilightSettings settings)
        {
            this.settings = settings;
        }

        public async System.Threading.Tasks.Task<bool> StartAsync(IntPtr windowHandle)
        {
            try
            {
                GraphicsCaptureItem? item;

                if (settings.AutoStartWithDefaultMonitor)
                {
                    // Ścieżka automatyczna: pomijamy natywne okno wyboru Windows
                    // i przechwytujemy główny monitor systemowy bezpośrednio z uchwytu HMONITOR.
                    try
                    {
                        IntPtr hmonitor = MonitorCaptureHelper.GetPrimaryMonitorHandle(windowHandle);
                        item = MonitorCaptureHelper.CreateItemForMonitor(hmonitor);
                        StatusChanged?.Invoke("Automatycznie wybrano główny monitor.");
                    }
                    catch (Exception autoEx)
                    {
                        StatusChanged?.Invoke(
                            $"Automatyczny wybór monitora nie powiódł się ({autoEx.Message}). Otwieram okno wyboru...");

                        var fallbackPicker = new GraphicsCapturePicker();
                        InitializeWithWindow.Initialize(fallbackPicker, windowHandle);
                        item = await fallbackPicker.PickSingleItemAsync();
                    }
                }
                else
                {
                    // Ścieżka manualna: pokazujemy natywny picker Windows, użytkownik wybiera monitor/okno.
                    var picker = new GraphicsCapturePicker();
                    InitializeWithWindow.Initialize(picker, windowHandle);
                    item = await picker.PickSingleItemAsync();
                }

                if (item == null)
                {
                    StatusChanged?.Invoke("Nie wybrano monitora.");
                    return false;
                }

                // Wybór generatora geometrii: niestandardowy (kreator per-bok) albo automatyczny,
                // proporcjonalny - w zależności od ustawienia UseCustomZoneLayout.
                CaptureZone[] zones;
                if (settings.UseCustomZoneLayout)
                {
                    zones = ZoneMapGenerator.Generate(
                        item.Size.Width, item.Size.Height,
                        settings.TopLedCount, settings.BottomLedCount,
                        settings.LeftLedCount, settings.RightLedCount,
                        settings.SamplingDepth,
                        settings.ZoneStartCorner, settings.ZoneStripDirection,
                        settings.ZoneShiftOffset, settings.ExcludedLedIndices);
                }
                else
                {
                    zones = ZoneMapGenerator.Generate(
                        item.Size.Width, item.Size.Height,
                        settings.LedCount, settings.SamplingDepth);
                }

                imageProcessor = new ImageProcessor(zones);
                imageProcessor.SetSmoothing(settings.SmoothingFactor);
                imageProcessor.SetQuality(settings.PixelSkipStep);

                ledSender?.Dispose();
                ledSender = new WledDdpNetworkSender(settings.EspIpAddress, zones.Length);

                captureEngine?.Dispose();
                captureEngine = new WgcCaptureEngine();

                pipelineManager?.Dispose();
                pipelineManager = new PipelineManager(captureEngine, imageProcessor, ledSender, settings, zones.Length);
                pipelineManager.Start();

                captureEngine.Start(item);

                stateWatcher?.Dispose();
                stateWatcher = new SystemStateWatcher(settings);
                stateWatcher.AmbientModeRequested += trigger =>
                {
                    var mode = trigger == SystemAmbientTrigger.LockOrSleep ? settings.LockScreenMode : settings.IdleMode;
                    pipelineManager?.EnterAmbientMode(mode);
                    StatusChanged?.Invoke($"Tryb ambientowy: {trigger} ({mode})");
                };
                stateWatcher.NormalModeRequested += () =>
                {
                    pipelineManager?.ExitAmbientMode();
                    StatusChanged?.Invoke("Przechwytywanie aktywne.");
                };

                IsRunning = true;
                StatusChanged?.Invoke($"Aktywne: {item.DisplayName} -> {settings.EspIpAddress}");
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Błąd: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            stateWatcher?.Dispose();
            stateWatcher = null;

            pipelineManager?.Dispose();
            pipelineManager = null;

            captureEngine?.Dispose();
            captureEngine = null;

            IsRunning = false;
            StatusChanged?.Invoke("Zatrzymano.");
        }

        public double CaptureFps => pipelineManager?.CurrentCaptureFps ?? 0;
        public double SendFps => pipelineManager?.CurrentSendFps ?? 0;

        public void ApplyLiveSettings()
        {
            imageProcessor?.SetSmoothing(settings.SmoothingFactor);
            imageProcessor?.SetQuality(settings.PixelSkipStep);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}