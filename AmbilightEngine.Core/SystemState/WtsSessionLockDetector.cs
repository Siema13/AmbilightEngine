using System;
using System.Runtime.InteropServices;

namespace AmbilightEngine.Core.SystemState
{
    // Autorytatywne wykrywanie blokady sesji przez WTSQuerySessionInformation z klasą
    // WTSSessionInfoEx. To jest oficjalnie dokumentowany mechanizm Windows do sprawdzenia
    // stanu blokady (SessionFlags == WTS_SESSIONSTATE_LOCK) - w przeciwieństwie do
    // OpenInputDesktop, które Microsoft wprost opisuje jako niewiarygodne/niedokumentowane
    // do tego celu.
    public static class WtsSessionLockDetector
    {
        private const int WTS_CURRENT_SESSION = -1;
        private const int WTSSessionInfoEx = 25;
        private const int WTS_SESSIONSTATE_LOCK = 0;
        private const int WTS_SESSIONSTATE_UNLOCK = 1;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        // Zwraca true, jeśli sesja jest aktualnie zablokowana. Zwraca false także wtedy,
        // gdy stan jest nieznany (WTS_SESSIONSTATE_UNKNOWN) lub odczyt się nie powiódł -
        // w takim wypadku wywołujący powinien traktować to jako "brak zmiany".
        public static bool? TryIsSessionLocked()
        {
            IntPtr buffer = IntPtr.Zero;

            try
            {
                bool success = WTSQuerySessionInformation(
                    IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx, out buffer, out uint bytesReturned);

                if (!success || buffer == IntPtr.Zero || bytesReturned == 0)
                {
                    return null;
                }

                int level = Marshal.ReadInt32(buffer, 0);
                if (level != 1)
                {
                    return null;
                }

                // WTSINFOEX_LEVEL1: Level(4) + SessionId(4) + SessionState(4) + SessionFlags(4)
                // SessionFlags zaczyna się więc na offsecie 4 (Data) + 4 (SessionId) + 4 (SessionState) = 12.
                int sessionFlags = Marshal.ReadInt32(buffer, 12);

                if (sessionFlags == WTS_SESSIONSTATE_LOCK) return true;
                if (sessionFlags == WTS_SESSIONSTATE_UNLOCK) return false;

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    WTSFreeMemory(buffer);
                }
            }
        }
    }
}