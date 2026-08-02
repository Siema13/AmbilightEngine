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

    // Centralny orkiestrator potoku danych: Capture (Producer) -> Processing + Output (Consumer).
    // Wykorzystuje bezblokowy, bounded Channel (Single-Producer-Single-Consumer) zamiast lock/Monitor.
    // Jeśli konsument nie nadąża, najstarsze klatki są celowo odrzucane (DropOldest) - priorytetem jest
    // aktualność światła (low latency) nad kompletnością historii klatek.
    // Dodatkowo zarządza trybem ambientowym (Lounge Light) - niezależna, wolna pętla nadpisująca
    // normalny potok obrazu, aktywowana przez SystemStateWatcher przy blokadzie ekranu/bezczynności.
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
        private Task? ambientTask;
        private volatile bool isRunning;
        private volatile bool isAmbientModeActive;
        private AmbientLightMode currentAmbientMode = AmbientLightMode.Off;
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

            StopAmbientLoopIfActive();
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

        // Wstrzymuje normalny potok obrazu i przełącza wyjście na stały kolor Lounge Light,
        // wysyłany cyklicznie w tle - używane przy blokadzie ekranu / bezczynności systemu.
        public void EnterAmbientMode(AmbientLightMode mode)
        {
            currentAmbientMode = mode;

            if (mode == AmbientLightMode.Off)
            {
                StopAmbientLoopIfActive();
                SendBlackFrame();
                return;
            }

            if (isAmbientModeActive) return;

            isAmbientModeActive = true;
            ambientTask = Task.Run(() => AmbientLoopAsync(cts.Token));
        }

        // Wraca do normalnego przetwarzania klatek z ekranu.
        public void ExitAmbientMode()
        {
            StopAmbientLoopIfActive();
        }

        private void StopAmbientLoopIfActive()
        {
            if (!isAmbientModeActive) return;
            isAmbientModeActive = false;

            try
            {
                ambientTask?.Wait(millisecondsTimeout: 500);
            }
            catch (Exception)
            {
                // Best-effort - nie blokujemy wyjścia z trybu ambientowego przy timeoucie.
            }

            ambientTask = null;
        }

        private async Task AmbientLoopAsync(CancellationToken token)
        {
            Debug.WriteLine("[DIAG] AmbientLoopAsync wystartował.");

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));

                while (!token.IsCancellationRequested && isAmbientModeActive)
                {
                    if (currentAmbientMode == AmbientLightMode.LoungeLight)
                    {
                        var color = new RgbColor(settings.LoungeColorR, settings.LoungeColorG, settings.LoungeColorB);
                        var frame = new RgbColor[ledCount];
                        Array.Fill(frame, color);

                        try
                        {
                            outputDevice.SendFrame(frame);
                        }
                        catch (Exception)
                        {
                            // Błąd wysyłki jednej klatki ambientowej nie może zabić pętli.
                        }
                    }

                    await timer.WaitForNextTickAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                // Prawidłowe zamknięcie przy Stop() lub przełączeniu z powrotem na tryb normalny.
            }
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
        // Wysyła jednorazową komendę wyboru efektu do WLED. Wywoływane z UI przy zmianie
        // efektu, a nie z pętli ConsumerLoopAsync, bo firmware WLED sam podtrzymuje animację.
        public async Task<bool> ActivateWledEffectAsync(int fxId, int speed, int intensity)
        {
            if (outputDevice is WledDdpNetworkSender wledSender)
            {
                return await wledSender.SetEffectAsync(fxId, speed, intensity);
            }

            return false;
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