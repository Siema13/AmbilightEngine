using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace AmbilightEngine.Core.SystemState
{
    // NOWOŚĆ: sprawdza, czy jakakolwiek aplikacja w systemie (przeglądarka, VLC, Spotify,
    // itd.) ma aktywną sesję multimedialną w stanie odtwarzania (GlobalSystemMediaTransport-
    // ControlsSessionManager). Używane przez SystemStateWatcher, żeby NIE przechodzić w tryb
    // bezczynności (ambient) podczas aktywnego oglądania filmu, nawet jeśli użytkownik nie
    // rusza myszką/klawiaturą przez dłuższy czas - typowa sytuacja przy oglądaniu wideo.
    public static class MediaPlaybackDetector
    {
        private static GlobalSystemMediaTransportControlsSessionManager? cachedManager;

        public static async Task<bool> IsAnyMediaCurrentlyPlayingAsync()
        {
            try
            {
                cachedManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                var sessions = cachedManager.GetSessions();

                foreach (var session in sessions)
                {
                    var playbackInfo = session.GetPlaybackInfo();
                    if (playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                // Best-effort: jeśli API nie jest dostępne (np. starszy build Windows) lub
                // wystąpi błąd COM, po prostu zakładamy "brak odtwarzania" - nie może to
                // zablokować całego mechanizmu wykrywania bezczynności.
                return false;
            }
        }
    }
}