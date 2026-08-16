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
    //
    // NOWOŚĆ: dla trybów opartych na strumieniu DDP (Video Sync, Static Color) zarządzana jest
    // dodatkowo trwała sesja "live" WLED (patrz UpdateRealtimeSession) - eliminuje to zależność
    // Video Sync od skonfigurowanego w WLED Realtime Timeout przy dłuższych przerwach w
    // dostarczaniu klatek z Windows Graphics Capture (np. całkowicie statyczny obraz na ekranie).
    public sealed class PipelineManager : IDisposable
    {
        private readonly ICaptureSource captureSource;
        private readonly IOutputDevice outputDevice;
        private readonly AmbilightSettings settings;
        private readonly Channel<FrameEnvelope> channel;
        private readonly CancellationTokenSource cts;
        private readonly Stopwatch fpsStopwatch = new Stopwatch();
        private readonly Stopwatch frameGapStopwatch = new Stopwatch();
        private const int StutterWarningThresholdMs = 150;

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
        private volatile bool isRealtimeSessionActive;
        private DisplayStateSnapshot? preAmbientSnapshot;
        private CancellationTokenSource? ambientEffectCts;
        private bool isDisposed;
        private readonly object transitionLock = new();
        private CancellationTokenSource? transitionCts;
        private RgbColor[]? lastSentFrame;
        private RgbColor[]? pendingVideoSyncStartFrame;
        private volatile bool isTransitionActive;
        private volatile bool isWaitingForVideoSyncFrame;
        private long transitionGeneration;
        private const int TransitionDurationMs = 180;
        private const int TransitionFrameIntervalMs = 25;
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
            blackBarDetector.BlackThreshold = settings.BlackBarThreshold;
            blackBarDetector.MinBlackRatio = settings.BlackBarMinRatio;
        }

        public void SetBlackBarDetectionEnabled(bool enabled)
        {
            blackBarDetector.IsEnabled = enabled;
        }

        public void SetBlackBarDetectionParameters(byte threshold, double minRatio)
        {
            blackBarDetector.BlackThreshold = threshold;
            blackBarDetector.MinBlackRatio = minRatio;
        }

        public void Start()
        {
            if (isRunning) return;
            isRunning = true;
            fpsStopwatch.Restart();

            outputDevice.Open();

            // NOWOŚĆ: włączamy trwałą sesję "live" WLED od razu, jeśli aktywny tryb korzysta
            // z ciągłego strumienia DDP (Video Sync / Static Color). W trybie WLED Effects
            // sesja live pozostaje wyłączona, żeby nie blokować natywnej animacji urządzenia.
            UpdateRealtimeSession(settings.ActiveDisplayMode != DisplayMode.WledEffects);

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

            lock (transitionLock)
            {
                transitionCts?.Cancel();
                transitionCts?.Dispose();
                transitionCts = null;
                isTransitionActive = false;
            }

            // NOWOŚĆ: zwalniamy sesję "live" WLED synchronicznie (z krótkim oczekiwaniem),
            // żeby urządzenie na pewno wróciło do stanu domyślnego przed zamknięciem gniazda UDP.
            UpdateRealtimeSession(false, waitForCompletion: true);

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

        // NOWOŚĆ: wywoływane z warstwy UI/hosta po każdej zmianie trybu wyświetlania (Video Sync /
        // Static Color / WLED Effects) dokonanej podczas działania przechwytywania (bez Stop/Start),
        // aby sesja "live" WLED zawsze odpowiadała aktualnie wybranemu trybowi.
        public void NotifyDisplayModeChanged()
        {
            if (!isRunning || isAmbientModeActive)
            {
                return;
            }

            UpdateRealtimeSession(settings.ActiveDisplayMode != DisplayMode.WledEffects);
        }

        // Wchodzi w tryb ambientowy: zapisuje snapshot aktualnego stanu wyświetlania,
        // a następnie jednorazowo wywołuje skonfigurowany efekt WLED (JSON API) - bez
        // ciągłego wysyłania DDP. Jeśli config.IsEnabled == false, diody są gaszone.
        public void EnterAmbientMode(AmbientEffectConfig config)
        {
            if (isAmbientModeActive) return;
            isAmbientModeActive = true;

            // NOWOŚĆ: tryb ambientowy nie korzysta z ciągłego DDP, więc zwalniamy sesję "live".
            UpdateRealtimeSession(false);

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
            if (!isAmbientModeActive)
            {
                return;
            }

            isAmbientModeActive = false;

            ambientEffectCts?.Cancel();

            // Pipeline wyłącznie kończy lokalne blokowanie ramek capture.
            // Przywrócenie Video Sync / Static Color / efektu WLED, sesji live i komend
            // JSON jest własnością AppEngineHost. Dwie równoległe ścieżki powodowały
            // anulowanie żądań WLED po Resume.
            preAmbientSnapshot = null;

            Debug.WriteLine(
                "[DIAG] PipelineManager: zakończono ambient; kontrola przywracania przekazana do AppEngineHost.");
        }

        // NOWOŚĆ: zarządza trwałą sesją "live" WLED (JSON API), niezależną od skonfigurowanego
        // w WLED Realtime Timeout. Zgodnie z dokumentacją WLED, ustawienie {"live":true} sprawia,
        // że urządzenie NIE wraca do lokalnego efektu, nawet jeśli przez dłuższą chwilę (np. przy
        // całkowicie statycznym obrazie z WGC) nie nadejdzie żaden kolejny pakiet DDP. Wywoływane
        // tylko dla trybów opartych na strumieniu DDP (Video Sync, Static Color) - w trybie WLED
        // Effects sesja live musi być wyłączona, aby nie blokować natywnej animacji.
        private void UpdateRealtimeSession(bool shouldBeActive, bool waitForCompletion = false)
        {
            if (outputDevice is not WledDdpNetworkSender wledSender)
            {
                return;
            }

            if (shouldBeActive == isRealtimeSessionActive)
            {
                return;
            }

            isRealtimeSessionActive = shouldBeActive;

            Task sessionTask = SetLiveOverrideSafeAsync(wledSender, shouldBeActive);

            if (waitForCompletion)
            {
                try
                {
                    sessionTask.Wait(TimeSpan.FromMilliseconds(800));
                }
                catch (Exception)
                {
                    // Best-effort: nie blokujemy zatrzymania silnika błędem komunikacji z WLED.
                }
            }
        }

        private static async Task SetLiveOverrideSafeAsync(WledDdpNetworkSender wledSender, bool enabled)
        {
            try
            {
                await wledSender.SetLiveOverrideAsync(enabled).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Błąd komunikacji z WLED przy zmianie sesji realtime nie może zabić silnika.
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

            // NOWOŚĆ: diagnostyka szarpania - mierzymy realny czas między dwoma kolejnymi
            // dostarczonymi klatkami z WGC. Jeśli przerwa jest podejrzanie długa (dłuższa
            // niż typowy pojedynczy klatka nawet przy 30 FPS), logujemy to w DIAG - pomaga
            // odróżnić, czy szarpanie pochodzi z samego przechwytywania (WGC/GPU/kompozytor),
            // czy dopiero z dalszej części pipeline (przetwarzanie obrazu, wysyłka DDP po Wi-Fi).
            if (frameGapStopwatch.IsRunning)
            {
                long gapMs = frameGapStopwatch.ElapsedMilliseconds;
                if (gapMs > StutterWarningThresholdMs)
                {
                    Debug.WriteLine(
                        $"[DIAG] PipelineManager: wykryto przerwę w dostarczaniu klatek z WGC: {gapMs} ms " +
                        $"(próg={StutterWarningThresholdMs} ms) - możliwe szarpanie po stronie przechwytywania.");
                }
            }
            frameGapStopwatch.Restart();

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
                            continue;
                        }

                        DisplayMode activeMode = settings.ActiveDisplayMode;

                        if (activeMode == DisplayMode.StaticColor)
                        {
                            if (!isTransitionActive)
                            {
                                SendStaticColorFrame();
                            }

                            Interlocked.Increment(ref framesSent);
                            continue;
                        }

                        if (activeMode == DisplayMode.WledEffects)
                        {
                            Interlocked.Increment(ref framesSent);
                            continue;
                        }

                        if (settings.EnableBlackBarDetection)
                        {
                            BlackBarInsets insets = blackBarDetector.Detect(
                                envelope.RentedBuffer,
                                envelope.Width,
                                envelope.Height,
                                envelope.Stride);

                            if (insets.Top != lastReportedInsets.Top ||
                                insets.Bottom != lastReportedInsets.Bottom ||
                                insets.Left != lastReportedInsets.Left ||
                                insets.Right != lastReportedInsets.Right)
                            {
                                lastReportedInsets = insets;
                                BlackBarInsetsChanged?.Invoke(insets);
                            }
                        }

                        ImageProcessor activeProcessor = Volatile.Read(ref imageProcessor);

                        ReadOnlySpan<RgbColor> processed = activeProcessor.ProcessFrame(
                            envelope.RentedBuffer.AsSpan(0, envelope.DataLength),
                            envelope.Stride,
                            envelope.Width,
                            envelope.Height);

                        // Ten warunek MUSI wystąpić przed isTransitionActive.
                        // To pierwsza klatka po wejściu w Video Sync, od której ma zacząć się fade.
                        if (isWaitingForVideoSyncFrame)
                        {
                            StartVideoSyncFrameTransition(processed);

                            Interlocked.Increment(ref framesSent);
                            continue;
                        }

                        // W trakcie fade ramki DDP wysyła tylko zadanie StartFrameTransition.
                        if (isTransitionActive)
                        {
                            Interlocked.Increment(ref framesSent);
                            continue;
                        }

                        SendAndRememberFrame(processed);
                        Interlocked.Increment(ref framesSent);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[DIAG] PipelineManager: błąd obsługi jednej klatki: {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(envelope.RentedBuffer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normalne zakończenie potoku przy Stop().
            }
        }

        private void SendStaticColorFrame()
        {
            try
            {
                var color = new RgbColor(settings.StaticColorR, settings.StaticColorG, settings.StaticColorB);
                var frame = new RgbColor[ledCount];
                Array.Fill(frame, color);
                SendAndRememberFrame(frame);
            }
            catch (Exception)
            {
                // Blad wysylki stalego koloru nie moze zabic konsumenta.
            }
        }
        public void TransitionToStaticColor(byte red, byte green, byte blue)
        {
            settings.StaticColorR = red;
            settings.StaticColorG = green;
            settings.StaticColorB = blue;
            settings.ActiveDisplayMode = DisplayMode.StaticColor;

            NotifyDisplayModeChanged();

            var targetColor = new RgbColor(red, green, blue);
            var targetFrame = new RgbColor[ledCount];
            Array.Fill(targetFrame, targetColor);

            StartFrameTransition(targetFrame, () =>
            {
                Debug.WriteLine(
                    $"[DIAG] PipelineManager: zakończono płynne przejście do Static Color RGB({red}, {green}, {blue}).");
            });
        }

        public void TransitionToVideoSync()
        {
            lock (transitionLock)
            {
                transitionCts?.Cancel();
                transitionCts?.Dispose();
                transitionCts = null;

                Interlocked.Increment(ref transitionGeneration);

                pendingVideoSyncStartFrame = GetLastFrameOrBlack();
                isWaitingForVideoSyncFrame = true;
                isTransitionActive = true;
            }

            settings.ActiveDisplayMode = DisplayMode.VideoSync;
            NotifyDisplayModeChanged();

            Debug.WriteLine(
                "[DIAG] PipelineManager: oczekiwanie na pierwszą klatkę Video Sync do rozpoczęcia przejścia.");
        }
        private void StartVideoSyncFrameTransition(ReadOnlySpan<RgbColor> firstVideoSyncFrame)
        
        {
            Debug.WriteLine(
    "[DIAG] PipelineManager: odebrano pierwszą klatkę Video Sync; uruchamiam fade.");
            RgbColor[] startFrame;
            RgbColor[] targetFrame;

            lock (transitionLock)
            {
                if (!isWaitingForVideoSyncFrame)
                {
                    return;
                }

                isWaitingForVideoSyncFrame = false;
                startFrame = pendingVideoSyncStartFrame is { Length: var length } && length == ledCount
                    ? (RgbColor[])pendingVideoSyncStartFrame.Clone()
                    : GetLastFrameOrBlack();

                pendingVideoSyncStartFrame = null;
                targetFrame = firstVideoSyncFrame.ToArray();
            }

            StartFrameTransition(targetFrame, onCompleted: () =>
            {
                Debug.WriteLine(
                    "[DIAG] PipelineManager: zakończono płynne przejście do Video Sync.");
            }, startFrame);
        }
        private void StartFrameTransition(
    RgbColor[] targetFrame,
    Action onCompleted,
    RgbColor[]? explicitStartFrame = null)
        {
            if (!isRunning || targetFrame.Length != ledCount)
            {
                return;
            }
            Debug.WriteLine(
    $"[DIAG] FADE START: mode={settings.ActiveDisplayMode}, " +
    $"duration={TransitionDurationMs} ms, leds={ledCount}.");
            RgbColor[] startFrame;
            CancellationToken token;
            long generation;

            lock (transitionLock)
            {
                transitionCts?.Cancel();
                transitionCts?.Dispose();

                transitionCts = new CancellationTokenSource();
                token = transitionCts.Token;

                generation = Interlocked.Increment(ref transitionGeneration);
                startFrame = explicitStartFrame ?? GetLastFrameOrBlack();

                isWaitingForVideoSyncFrame = false;
                isTransitionActive = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    const int frameCount = TransitionDurationMs / TransitionFrameIntervalMs;
                    var interpolatedFrame = new RgbColor[ledCount];

                    for (int step = 1; step <= frameCount; step++)
                    {
                        token.ThrowIfCancellationRequested();

                        double linearProgress = step / (double)frameCount;

                        // Ease-out: start łagodny, końcówka stabilna; ogranicza widoczne skoki.
                        double progress = 1.0 - Math.Pow(1.0 - linearProgress, 3.0);

                        InterpolateFrames(startFrame, targetFrame, interpolatedFrame, progress);
                        Debug.WriteLine(
    $"[DIAG] FADE FRAME {step}/{frameCount}, progress={progress:F2}.");
                        SendAndRememberFrame(interpolatedFrame);

                        await Task.Delay(TransitionFrameIntervalMs, token)
                            .ConfigureAwait(false);
                    }

                    if (!token.IsCancellationRequested &&
                        Volatile.Read(ref transitionGeneration) == generation)
                    {
                        onCompleted();
                        Debug.WriteLine("[DIAG] FADE END.");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Zostało rozpoczęte nowsze przejście.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[DIAG] PipelineManager: błąd płynnego przejścia: {ex.Message}");
                }
                finally
                {
                    if (!token.IsCancellationRequested &&
                        Volatile.Read(ref transitionGeneration) == generation)
                    {
                        lock (transitionLock)
                        {
                            if (Volatile.Read(ref transitionGeneration) == generation)
                            {
                                isTransitionActive = false;
                            }
                        }
                    }
                }
            }, CancellationToken.None);
        }

        private RgbColor[] GetLastFrameOrBlack()
        {
            if (lastSentFrame is { Length: var length } && length == ledCount)
            {
                return (RgbColor[])lastSentFrame.Clone();
            }

            return new RgbColor[ledCount];
        }

        private void SendAndRememberFrame(ReadOnlySpan<RgbColor> frame)
        {
            outputDevice.SendFrame(frame);

            if (lastSentFrame is null || lastSentFrame.Length != ledCount)
            {
                lastSentFrame = new RgbColor[ledCount];
            }

            frame.CopyTo(lastSentFrame);
        }

        private static void InterpolateFrames(
            ReadOnlySpan<RgbColor> start,
            ReadOnlySpan<RgbColor> target,
            Span<RgbColor> destination,
            double progress)
        {
            for (int index = 0; index < destination.Length; index++)
            {
                RgbColor from = start[index];
                RgbColor to = target[index];

                destination[index] = new RgbColor(
                    (byte)Math.Round(from.R + (to.R - from.R) * progress),
                    (byte)Math.Round(from.G + (to.G - from.G) * progress),
                    (byte)Math.Round(from.B + (to.B - from.B) * progress));
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

            lock (transitionLock)
            {
                transitionCts?.Cancel();
                transitionCts?.Dispose();
                transitionCts = null;
            }

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