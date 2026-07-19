using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AmbilightEngine.Core.Capture;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Core.SystemState;

namespace AmbilightEngine.Core.Pipeline
{
    public readonly struct FramePayload
    {
        public readonly RgbColor[] Colors;

        public FramePayload(RgbColor[] colors)
        {
            Colors = colors;
        }
    }

    public sealed class PipelineManager : IDisposable
    {
        private readonly WgcCaptureEngine captureEngine;
        private readonly ImageProcessor imageProcessor;
        private readonly IOutputDevice outputDevice;
        private readonly AmbilightSettings settings;
        private readonly int ledCount;

        private readonly Channel<FramePayload> channel;
        private readonly LoungeLightEffectGenerator ambientGenerator;

        private readonly object stateLock = new object();

        private CancellationTokenSource? cancellationSource;
        private Task? consumerTask;
        private Task? ambientTask;
        private Task? blackoutKeepAliveTask;
        private CancellationTokenSource? blackoutCts;

        private volatile bool isAmbientModeActive;
        private volatile bool isDisposed;
        private AmbientLightMode currentAmbientMode = AmbientLightMode.Off;
        private RgbColor lastCapturedColor = new RgbColor(0, 0, 0);

        private long framesCaptured;
        private long framesSent;
        private readonly Stopwatch fpsStopwatch = new Stopwatch();

        public double CurrentCaptureFps { get; private set; }
        public double CurrentSendFps { get; private set; }
        public bool IsAmbientModeActive => isAmbientModeActive;

        public PipelineManager(WgcCaptureEngine captureEngine, ImageProcessor imageProcessor, IOutputDevice outputDevice, AmbilightSettings settings, int ledCount)
        {
            this.captureEngine = captureEngine;
            this.imageProcessor = imageProcessor;
            this.outputDevice = outputDevice;
            this.settings = settings;
            this.ledCount = ledCount;

            var options = new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            channel = Channel.CreateBounded<FramePayload>(options);
            ambientGenerator = new LoungeLightEffectGenerator(ledCount);
        }

        public void Start()
        {
            lock (stateLock)
            {
                if (isDisposed) return;

                // Jeśli działał w tle blackout keep-alive (po poprzednim Stop), zatrzymujemy go
                // przed ponownym otwarciem urządzenia i rozpoczęciem właściwego przechwytywania.
                StopBlackoutKeepAliveLocked();

                outputDevice.Open();

                cancellationSource = new CancellationTokenSource();
                fpsStopwatch.Restart();

                captureEngine.OnFrameCaptured += OnFrameCaptured;
                consumerTask = Task.Run(() => ConsumerLoopAsync(cancellationSource.Token));
            }
        }

        public void Stop()
        {
            CancellationTokenSource? sourceToCancel;

            lock (stateLock)
            {
                if (isDisposed) return;

                captureEngine.OnFrameCaptured -= OnFrameCaptured;
                sourceToCancel = cancellationSource;
            }

            try
            {
                sourceToCancel?.Cancel();
                consumerTask?.Wait(TimeSpan.FromSeconds(2));
                ambientTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Ignorujemy timeout lub OperationCanceledException przy wygaszaniu.
            }

            lock (stateLock)
            {
                if (isDisposed) return;

                // Kluczowa zmiana: NIE zamykamy urządzenia (outputDevice.Close()) i NIE próbujemy
                // wyłączać WLED komendami JSON. WLED w trybie realtime po ~2.5s bez danych
                // wraca do poprzedniego (jasnego) presetu - to jest dokumentowane zachowanie
                // firmware, nie da się tego pewnie obejść komendami "on"/"live" (GitHub #3720, #3589).
                // Zamiast tego utrzymujemy urządzenie w trybie realtime, wysyłając ciągłe
                // czarne ramki w tle, aż do następnego Start() - to gwarantuje, że diody
                // zostają trwale zgaszone.
                StartBlackoutKeepAliveLocked();
            }
        }

        // Musi być wywołane pod stateLock.
        private void StartBlackoutKeepAliveLocked()
        {
            if (blackoutKeepAliveTask != null && !blackoutKeepAliveTask.IsCompleted) return;

            blackoutCts = new CancellationTokenSource();
            var token = blackoutCts.Token;

            blackoutKeepAliveTask = Task.Run(() => BlackoutKeepAliveLoopAsync(token));
        }

        // Musi być wywołane pod stateLock.
        private void StopBlackoutKeepAliveLocked()
        {
            try
            {
                blackoutCts?.Cancel();
                blackoutKeepAliveTask?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception)
            {
                // Ignorujemy - to tylko zatrzymanie wątku w tle.
            }
            finally
            {
                blackoutCts?.Dispose();
                blackoutCts = null;
                blackoutKeepAliveTask = null;
            }
        }

        private async Task BlackoutKeepAliveLoopAsync(CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine("[DIAG] BlackoutKeepAliveLoopAsync wystartował - trzymam WLED w czerni.");

            var blackFrame = new RgbColor[ledCount];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        outputDevice.SendFrame(blackFrame);
                    }
                    catch (Exception)
                    {
                        // Ignorujemy pojedynczy błąd wysyłki - kolejna ramka pójdzie za chwilę.
                    }

                    // Wysyłamy co 1 sekundę - znacznie częściej niż domyślny timeout realtime WLED (2.5s),
                    // więc urządzenie nigdy nie zdąży wrócić do poprzedniego presetu.
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
            catch (OperationCanceledException)
            {
                // Prawidłowe zamknięcie przy Start() lub Dispose().
            }

            System.Diagnostics.Debug.WriteLine("[DIAG] BlackoutKeepAliveLoopAsync zakończony.");
        }

        public void EnterAmbientMode(AmbientLightMode mode)
        {
            lock (stateLock)
            {
                if (isDisposed || cancellationSource == null || cancellationSource.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine("[DIAG] EnterAmbientMode zignorowane - pipeline jest zatrzymany lub w trakcie dysponowania.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[DIAG] EnterAmbientMode wywołane, mode: {mode}");
                currentAmbientMode = mode;
                isAmbientModeActive = true;

                if (mode == AmbientLightMode.Off)
                {
                    channel.Writer.TryWrite(new FramePayload(new RgbColor[ledCount]));
                    return;
                }

                ambientGenerator.BeginFadeIn(lastCapturedColor);

                if (ambientTask == null || ambientTask.IsCompleted)
                {
                    var token = cancellationSource.Token;
                    ambientTask = Task.Run(() => AmbientLoopAsync(token));
                }
            }
        }

        public void ExitAmbientMode()
        {
            isAmbientModeActive = false;
        }

        private void OnFrameCaptured(ReadOnlySpan<byte> rawPixels, int width, int height, int stride)
        {
            if (isAmbientModeActive || isDisposed) return;

            ReadOnlySpan<RgbColor> processedColors = imageProcessor.ProcessFrame(rawPixels, stride);
            var colorsCopy = processedColors.ToArray();

            if (colorsCopy.Length > 0)
            {
                lastCapturedColor = colorsCopy[0];
            }

            channel.Writer.TryWrite(new FramePayload(colorsCopy));

            Interlocked.Increment(ref framesCaptured);
            UpdateFpsCounters();
        }

        private async Task AmbientLoopAsync(CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            System.Diagnostics.Debug.WriteLine("[DIAG] AmbientLoopAsync wystartował.");
            double lastElapsed = 0;

            using var periodicTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));

            try
            {
                while (!token.IsCancellationRequested && isAmbientModeActive && currentAmbientMode == AmbientLightMode.LoungeLight)
                {
                    double nowElapsed = stopwatch.Elapsed.TotalSeconds;
                    double delta = nowElapsed - lastElapsed;
                    lastElapsed = nowElapsed;

                    var frame = ambientGenerator.GenerateNextFrame(delta, settings.LoungeColorR, settings.LoungeColorG, settings.LoungeColorB);
                    channel.Writer.TryWrite(new FramePayload(frame.ToArray()));

                    await periodicTimer.WaitForNextTickAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                // Prawidłowe zamknięcie przy Stop() lub przełączeniu z powrotem na tryb normalny.
            }
        }

        private async Task ConsumerLoopAsync(CancellationToken token)
        {
            try
            {
                await foreach (FramePayload payload in channel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        outputDevice.SendFrame(payload.Colors);
                        Interlocked.Increment(ref framesSent);
                    }
                    catch (Exception)
                    {
                        // Ignorujemy pojedynczy błąd wysyłki.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Prawidłowe zamknięcie potoku.
            }
        }

        private void UpdateFpsCounters()
        {
            if (fpsStopwatch.ElapsedMilliseconds >= 1000)
            {
                CurrentCaptureFps = framesCaptured / (fpsStopwatch.ElapsedMilliseconds / 1000.0);
                CurrentSendFps = framesSent / (fpsStopwatch.ElapsedMilliseconds / 1000.0);

                Interlocked.Exchange(ref framesCaptured, 0);
                Interlocked.Exchange(ref framesSent, 0);
                fpsStopwatch.Restart();
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (isDisposed) return;
                isDisposed = true;
            }

            Stop();

            lock (stateLock)
            {
                StopBlackoutKeepAliveLocked();

                // Dopiero teraz, przy prawdziwym wyjściu z aplikacji, fizycznie zamykamy urządzenie.
                try
                {
                    outputDevice.Close();
                }
                catch (Exception)
                {
                    // Urządzenie mogło już być niedostępne - ignorujemy.
                }

                cancellationSource?.Dispose();
                cancellationSource = null;
            }
        }
    }
}