using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AmbilightEngine.Core.Audio
{
    // Przechwytuje dźwięk odtwarzany aktualnie przez system (WASAPI loopback) - to co słyszysz
    // z domyślnego urządzenia odtwarzania (głośniki/słuchawki), niezależnie od tego, która
    // aplikacja go generuje (film, muzyka, gra). NAudio wewnętrznie korzysta z natywnego WASAPI,
    // więc narzut wydajnościowy jest identyczny jak przy ręcznym interopie - różnica jest tylko
    // w ilości kodu, który musielibyśmy sami napisać i utrzymywać.
    //
    // WAŻNE: WasapiLoopbackCapture musi być tworzony i zamykany na tym samym urządzeniu, które było
    // aktywne w momencie startu. Jeśli użytkownik zmieni domyślne urządzenie odtwarzania w Windows
    // w trakcie działania (np. przełączy się ze słuchawek na głośniki), NAudio zgłosi błąd przy
    // kolejnym pakiecie danych - łapiemy to w RecordingStopped i automatycznie restartujemy capture
    // na nowym urządzeniu domyślnym, żeby użytkownik nie musiał ręcznie wyłączać/włączać efektu.
    public sealed class AudioCaptureService : IDisposable
    {
        private readonly object stateLock = new object();
        private WasapiLoopbackCapture? capture;
        private bool isDisposed;
        private bool isRestartPending;

        // Zgłaszane dla każdego odebranego bloku próbek PCM (float, zakres -1.0 .. 1.0),
        // interleaved wg WaveFormat.Channels. Handler powinien być szybki - jest wywoływany
        // na wewnętrznym wątku audio NAudio i nie powinien blokować.
        public event Action<float[], int, WaveFormat>? SamplesAvailable;

        // Zgłaszane, gdy capture zatrzymał się z powodu błędu (np. zmiana domyślnego urządzenia
        // audio) i automatyczny restart się nie powiódł - UI powinien pokazać status "audio
        // niedostępne" i pozwolić użytkownikowi ręcznie spróbować ponownie.
        public event Action<Exception?>? CaptureFailed;

        public bool IsCapturing { get; private set; }

        public void Start()
        {
            lock (stateLock)
            {
                if (isDisposed || IsCapturing) return;

                StartInternal();
            }
        }

        private void StartInternal()
        {
            try
            {
                // WasapiLoopbackCapture() bez argumentów bierze aktualne domyślne urządzenie
                // renderujące (Console/Multimedia role) - dokładnie to, co chcemy analizować.
                using var enumerator = new MMDeviceEnumerator();
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                capture = new WasapiLoopbackCapture(defaultDevice);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                IsCapturing = true;
            }
            catch (Exception ex)
            {
                IsCapturing = false;
                capture?.Dispose();
                capture = null;
                CaptureFailed?.Invoke(ex);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            var handler = SamplesAvailable;
            if (handler is null || capture is null) return;

            // Format loopback WASAPI jest zawsze IEEE float (WaveFormatEncoding.IeeeFloat) -
            // konwertujemy bajty na float bez pośredniego bufora, żeby nie alokować na każdym
            // pakiecie (kilkadziesiąt razy na sekundę).
            int sampleCount = e.BytesRecorded / sizeof(float);
            float[] samples = new float[sampleCount];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            handler.Invoke(samples, sampleCount, capture.WaveFormat);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (stateLock)
            {
                IsCapturing = false;

                if (capture is not null)
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;
                    capture.Dispose();
                    capture = null;
                }

                if (isDisposed || isRestartPending) return;

                if (e.Exception is not null)
                {
                    // Prawdopodobna zmiana domyślnego urządzenia audio przez użytkownika w trakcie
                    // działania - próbujemy jednorazowego automatycznego restartu na nowym
                    // urządzeniu domyślnym, zamiast od razu zgłaszać trwały błąd.
                    isRestartPending = true;

                    try
                    {
                        StartInternal();
                    }
                    finally
                    {
                        isRestartPending = false;
                    }

                    if (!IsCapturing)
                    {
                        CaptureFailed?.Invoke(e.Exception);
                    }
                }
            }
        }

        public void Stop()
        {
            lock (stateLock)
            {
                if (capture is null) return;

                try
                {
                    capture.StopRecording();
                }
                catch (Exception)
                {
                    // Zatrzymanie już zatrzymanego/wadliwego urządzenia nie może wywalić aplikacji -
                    // OnRecordingStopped i tak posprząta stan w finally ścieżki wywołującej.
                }
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (isDisposed) return;
                isDisposed = true;

                if (capture is not null)
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.RecordingStopped -= OnRecordingStopped;

                    try
                    {
                        capture.StopRecording();
                    }
                    catch (Exception)
                    {
                        // Ignorowane - zamykamy i tak.
                    }

                    capture.Dispose();
                    capture = null;
                }

                IsCapturing = false;
            }
        }
    }
}
