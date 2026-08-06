using System;
using System.Buffers;
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
    // Kopia klatki przekazywana bezpiecznie między wątkiem producenta (Capture) i konsumenta (Processing+Output).
    // Bufor jest wynajmowany z ArrayPool i MUSI być zwrócony przez konsumenta po użyciu.
    internal readonly struct FrameEnvelope
    {
        public readonly byte[] RentedBuffer;
        public readonly int DataLength;
        public readonly int Stride;
        public readonly int Width;
        public readonly int Height;

        public FrameEnvelope(byte[] rentedBuffer, int dataLength, int stride, int width, int height)
        {
            RentedBuffer = rentedBuffer;
            DataLength = dataLength;
            Stride = stride;
            Width = width;
            Height = height;
        }
    }

    // Snapshot stanu wyświetlania sprzed wejścia w tryb ambientowy - potrzebny do
    // dokładnego przywrócenia (VideoSync / StaticColor / WledEffects z parametrami).
    internal readonly struct DisplayStateSnapshot
    {
        public readonly DisplayMode Mode;
        public readonly int WledEffectId;
        public readonly int WledPaletteId;
        public readonly int WledSpeed;
        public readonly int WledIntensity;
        public readonly int WledBrightness;
        public readonly (byte R, byte G, byte B) WledPrimaryColor;
        public readonly (byte R, byte G, byte B) WledSecondaryColor;

        public DisplayStateSnapshot(
            DisplayMode mode,
            int wledEffectId, int wledPaletteId, int wledSpeed, int wledIntensity, int wledBrightness,
            (byte R, byte G, byte B) wledPrimaryColor, (byte R, byte G, byte B) wledSecondaryColor)
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
    }

    // Centralny orkiestrator potoku danych: Capture (Producer) -> Processing + Output (Consumer).
    // Wykorzystuje bezblokowy, bounded Channel (Single-Producer-Single-Consumer) zamiast lock/Monitor.
    // Jeśli konsument nie nadąża, najstarsze klatki są celowo odrzucane (DropOldest) - priorytetem jest
    // aktualność światła (low latency) nad kompletnością historii klatek.
    //
    // Tryb ambientowy (blokada ekranu / bezczynność) NIE wysyła już cyklicznie DDP - zamiast tego
    // jednorazowo wywołuje natywny efekt WLED przez JSON API (SetEffectAsync). Firmware WLED sam
    // podtrzymuje animację lokalnie, co eliminuje problem z timeoutem realtime i migotaniem.
    public sealed class PipelineManager : IDisposable
    {
        private readonly ICaptureSource captureSource;
        private readonly IOutputDevice outputDevice;
        private readonly AmbilightSettings settings;
        private readonly Channel<FrameEnvelope> channel;
        private readonly CancellationTokenSource cts;
        private readonly Stopwatch fpsStopwatch = new Stopwatch();

        // Liczba diod ustalona przy budowie potoku - przechowywana lokalnie, ponieważ
        // IOutputDevice (kontrakt ogólny) nie deklaruje właściwości LedCount.
        private readonly int ledCount;

        private readonly BlackBarDetectionService blackBarDetector = new BlackBarDetectionService();
        private BlackBarInsets lastReportedInsets = BlackBarInsets.None;

        public event Action<BlackBarInsets>? BlackBarInsetsChanged;

        private ImageProcessor imageProcessor;
        private Task? consumerTask;
        private volatile bool isRunning;
        private volatile bool isAmbientModeActive;
        private DisplayStateSnapshot? preAmbientSnapshot;
        private CancellationTokenSource? ambientEffectCts;
        private bool isDisposed;

        private long framesCaptured;
        private long framesSent;
        public double CurrentCaptureFps { get; private set; }
        public double CurrentSendFps { get; private set; }

        public PipelineManager(ICaptureSource captureSource, ImageProcessor imageProcessor, IOutputDevice outputDevice, AmbilightSettings settings, int ledCount)
        {
            this.captureSource = captureSource ?? throw new ArgumentNullException(nameof(captureSource));
            this.imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
            this.outputDevice = outputDevice ?? throw new ArgumentNullException(nameof(outputDevice));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.ledCount = ledCount;

            var options = new BoundedChannelOptions(capacity: 2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            };
            channel = Channel.CreateBounded<FrameEnvelope>(options);
            cts = new CancellationTokenSource();
        }

        public void SetBlackBarDetectionEnabled(bool enabled)
        {
            blackBarDetector.IsEnabled = enabled;
        }

        public void Start()
        {
            if (isRunning) return;
            isRunning = true;
            fpsStopwatch.Restart();

            outputDevice.Open();
            captureSource.OnFrameCaptured += OnFrameCapturedFromCaptureThread;
            consumerTask = Task.Run(() => ConsumerLoopAsync(cts.Token));

            _ = Task.Run(FpsCounterLoopAsync);
        }

        public void Stop()
        {
            if (!isRunning) return;
            isRunning = false;

            ambientEffectCts?.Cancel();
            isAmbientModeActive = false;

            captureSource.OnFrameCaptured -= OnFrameCapturedFromCaptureThread;
            channel.Writer.TryComplete();

            try
            {
                consumerTask?.Wait(millisecondsTimeout: 2000);
            }
            catch (AggregateException)
            {
                // Konsument zakończył się przez OperationCanceledException - oczekiwane przy Stop().
            }
        }

        // Podmienia procesor obrazu "na żywo" (nowa geometria stref albo nowy profil DSP),
        // bez zatrzymywania przechwytywania ekranu ani konsumenta.
        public void ReplaceImageProcessor(ImageProcessor newProcessor)
        {
            if (newProcessor == null) throw new ArgumentNullException(nameof(newProcessor));
            Interlocked.Exchange(ref imageProcessor, newProcessor);
        }

        // Wchodzi w tryb ambientowy: zapisuje snapshot aktualnego stanu wyświetlania,
        // a następnie jednorazowo wywołuje skonfigurowany efekt WLED (JSON API) - bez
        // ciągłego wysyłania DDP. Jeśli config.IsEnabled == false, diody są gaszone.
        public void EnterAmbientMode(AmbientEffectConfig config)
        {
            if (isAmbientModeActive) return;
            isAmbientModeActive = true;

            preAmbientSnapshot = new DisplayStateSnapshot(
                settings.ActiveDisplayMode,
                settings.LastWledEffectId, settings.LastWledPaletteId,
                settings.LastWledSpeed, settings.LastWledIntensity, settings.LastWledBrightness,
                (settings.LastWledPrimaryColorR, settings.LastWledPrimaryColorG, settings.LastWledPrimaryColorB),
                (settings.LastWledSecondaryColorR, settings.LastWledSecondaryColorG, settings.LastWledSecondaryColorB));

            ambientEffectCts?.Cancel();
            ambientEffectCts?.Dispose();
            ambientEffectCts = new CancellationTokenSource();
            var token = ambientEffectCts.Token;

            if (config == null || !config.IsEnabled)
            {
                // FIX: nie wysyłamy czarnej ramki DDP, jeśli aktualnie aktywny jest tryb WledEffects -
                // DDP w tym trybie nie jest w ogóle używane do renderowania, a jednorazowy pakiet
                // przełącza WLED w tryb realtime na czas "realtime timeout" firmware, przerywając
                // lokalnie renderowany efekt (obserwowane jako okresowe mrugnięcie/długa przerwa,
                // proporcjonalna do skonfigurowanego timeoutu realtime w WLED).
                if (settings.ActiveDisplayMode != DisplayMode.WledEffects)
                {
                    SendBlackFrame();
                }

                isAmbientModeActive = false;
                preAmbientSnapshot = null;
                return;
            }

            if (outputDevice is WledDdpNetworkSender wledSender)
            {
                var primary = (config.PrimaryColorR, config.PrimaryColorG, config.PrimaryColorB);
                var secondary = (config.SecondaryColorR, config.SecondaryColorG, config.SecondaryColorB);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await wledSender.SetEffectAsync(
    config.EffectId, config.Speed, config.Intensity, config.PaletteId,
    primary, secondary, config.Brightness, cancellationToken: token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Nowsze wejście/wyjście z ambientu anulowało to żądanie - oczekiwane.
                    }
                    catch (Exception)
                    {
                        // Błąd komunikacji z WLED przy wejściu w ambient nie może zabić silnika.
                    }
                }, token);
            }
        }

        // Wychodzi z trybu ambientowego i przywraca dokładnie ten tryb wyświetlania,
        // który był aktywny przed wejściem (VideoSync / StaticColor / WledEffects z parametrami).
        public void ExitAmbientMode()
        {
            if (!isAmbientModeActive) return;
            isAmbientModeActive = false;

            ambientEffectCts?.Cancel();

            if (preAmbientSnapshot is not DisplayStateSnapshot snapshot)
            {
                return;
            }

            settings.ActiveDisplayMode = snapshot.Mode;

            if (snapshot.Mode == DisplayMode.WledEffects && outputDevice is WledDdpNetworkSender wledSender)
            {
                var restoreCts = new CancellationTokenSource();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await wledSender.SetEffectAsync(
    snapshot.WledEffectId, snapshot.WledSpeed, snapshot.WledIntensity, snapshot.WledPaletteId,
    snapshot.WledPrimaryColor, snapshot.WledSecondaryColor, snapshot.WledBrightness,
    cancellationToken: restoreCts.Token);
                    }
                    catch (Exception)
                    {
                        // Błąd przywracania efektu po wyjściu z ambientu nie może zabić silnika.
                    }
                });
            }

            preAmbientSnapshot = null;
        }

        private void SendBlackFrame()
        {
            try
            {
                var blackFrame = new RgbColor[ledCount];
                outputDevice.SendFrame(blackFrame);
            }
            catch (Exception)
            {
                // Best-effort clear frame, nie blokujemy przełączenia trybu przez błąd sprzętowy.
            }
        }

        // Wywoływane synchronicznie z wątku WGC (Capture Thread) przy każdej nowej klatce.
        // Musi być ultra-szybkie: kopiujemy dane do wynajętego bufora i natychmiast wracamy.
        private void OnFrameCapturedFromCaptureThread(ReadOnlySpan<byte> rawPixels, int width, int height, int stride)
        {
            if (!isRunning || isAmbientModeActive) return;

            Interlocked.Increment(ref framesCaptured);

            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(rawPixels.Length);
            try
            {
                rawPixels.CopyTo(rentedBuffer);
                var envelope = new FrameEnvelope(rentedBuffer, rawPixels.Length, stride, width, height);

                if (!channel.Writer.TryWrite(envelope))
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
            }
            catch (Exception)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
                // Celowo pochłaniamy wyjątek na wątku Capture - jedna zgubiona klatka nie może
                // zawiesić silnika WGC.
            }
        }

        private async Task ConsumerLoopAsync(CancellationToken token)
        {
            try
            {
                await foreach (FrameEnvelope envelope in channel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        if (isAmbientModeActive)
                        {
                            // Tryb ambientowy jest obsługiwany przez jednorazowe SetEffectAsync -
                            // odrzucamy klatki przechwycone w tym czasie (nie powinno ich być, bo
                            // OnFrameCapturedFromCaptureThread też sprawdza isAmbientModeActive,
                            // ale to dodatkowe zabezpieczenie na wypadek wyścigu).
                            continue;
                        }

                        if (settings.ActiveDisplayMode == DisplayMode.StaticColor)
                        {
                            SendStaticColorFrame();
                            Interlocked.Increment(ref framesSent);
                            continue;
                        }

                        if (settings.ActiveDisplayMode == DisplayMode.WledEffects)
                        {
                            // W trybie efektów WLED urządzenie generuje animację lokalnie po stronie firmware -
                            // pomijamy przechwytywanie i przetwarzanie obrazu, żeby nie zużywać CPU bez potrzeby.
                            Interlocked.Increment(ref framesSent);
                            continue;
                        }

                        if (settings.EnableBlackBarDetection)
                        {
                            BlackBarInsets insets = blackBarDetector.Detect(
                                envelope.RentedBuffer, envelope.Width, envelope.Height, envelope.Stride);

                            if (insets.Top != lastReportedInsets.Top ||
                                insets.Bottom != lastReportedInsets.Bottom ||
                                insets.Left != lastReportedInsets.Left ||
                                insets.Right != lastReportedInsets.Right)
                            {
                                lastReportedInsets = insets;
                                BlackBarInsetsChanged?.Invoke(insets);
                            }
                        }

                        ReadOnlySpan<RgbColor> processed = imageProcessor.ProcessFrame(
                            envelope.RentedBuffer.AsSpan(0, envelope.DataLength),
                            envelope.Stride);

                        outputDevice.SendFrame(processed);
                        Interlocked.Increment(ref framesSent);
                    }
                    catch (Exception)
                    {
                        // Błąd przetwarzania jednej klatki nie może zabić wątku konsumenta.
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(envelope.RentedBuffer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Oczekiwane zamknięcie potoku przez Stop().
            }
        }

        private void SendStaticColorFrame()
        {
            try
            {
                var color = new RgbColor(settings.StaticColorR, settings.StaticColorG, settings.StaticColorB);
                var frame = new RgbColor[ledCount];
                Array.Fill(frame, color);
                outputDevice.SendFrame(frame);
            }
            catch (Exception)
            {
                // Blad wysylki stalego koloru nie moze zabic konsumenta.
            }
        }

        private async Task FpsCounterLoopAsync()
        {
            try
            {
                while (isRunning)
                {
                    await Task.Delay(1000, cts.Token);

                    double elapsedSeconds = fpsStopwatch.Elapsed.TotalSeconds;
                    if (elapsedSeconds <= 0) continue;

                    CurrentCaptureFps = Interlocked.Exchange(ref framesCaptured, 0) / elapsedSeconds;
                    CurrentSendFps = Interlocked.Exchange(ref framesSent, 0) / elapsedSeconds;
                    fpsStopwatch.Restart();
                }
            }
            catch (OperationCanceledException)
            {
                // Oczekiwane zamknięcie przy Dispose().
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            Stop();
            ambientEffectCts?.Dispose();
            cts.Cancel();
            cts.Dispose();

            try
            {
                outputDevice.Close();
            }
            catch (Exception)
            {
                // Urządzenie mogło już być niedostępne - ignorujemy.
            }
        }
    }
}