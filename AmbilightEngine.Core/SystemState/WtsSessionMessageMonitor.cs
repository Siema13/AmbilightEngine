using System;
using System.Runtime.InteropServices;

namespace AmbilightEngine.Core.SystemState
{
    // Nasłuchuje natywnego komunikatu WM_WTSSESSION_CHANGE bezpośrednio w kolejce
    // komunikatów głównego okna aplikacji (safe subclassing przez comctl32). To jest
    // najbardziej wiarygodny sposób wykrywania blokady/odblokowania ekranu - w
    // przeciwieństwie do SystemEvents.SessionSwitch (nigdy się nie odpalał w tej aplikacji)
    // czy OpenInputDesktop/WTSQuerySessionInformation (obie okazały się niewiarygodne przy
    // testach - druga zawsze zwracała "zablokowane", bo SessionFlags działa poprawnie
    // głównie na Windows Server/RDS, nie na kliencie). WinUI 3 pompuje realne komunikaty
    // Win32 swojego okna, więc podczepienie się pod nie jest w pełni skuteczne.
    public sealed class WtsSessionMessageMonitor : IDisposable
    {
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int WTS_SESSION_LOCK = 0x7;
        private const int WTS_SESSION_UNLOCK = 0x8;
        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private static readonly IntPtr SubclassId = new IntPtr(0x414D4249); // unikalny identyfikator ("AMBI").

        private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProcDelegate pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        private readonly IntPtr hwnd;
        // Ważne: delegat musi być przechowany jako pole, inaczej GC może go zwolnić,
        // podczas gdy natywny kod Win32 wciąż będzie próbował go wywołać (crash).
        private readonly SubclassProcDelegate subclassProcDelegate;
        private bool isRegistered;
        private bool isDisposed;

        public event Action? SessionLocked;
        public event Action? SessionUnlocked;

        public WtsSessionMessageMonitor(IntPtr hwnd)
        {
            this.hwnd = hwnd;
            subclassProcDelegate = SubclassWndProc;

            bool subclassOk = SetWindowSubclass(hwnd, subclassProcDelegate, SubclassId, IntPtr.Zero);
            bool notifyOk = WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION);
            isRegistered = subclassOk && notifyOk;

            System.Diagnostics.Debug.WriteLine(
                $"[DIAG] WtsSessionMessageMonitor: subclassOk={subclassOk}, notifyOk={notifyOk}");
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_WTSSESSION_CHANGE)
            {
                int code = wParam.ToInt32();

                if (code == WTS_SESSION_LOCK)
                {
                    SessionLocked?.Invoke();
                }
                else if (code == WTS_SESSION_UNLOCK)
                {
                    SessionUnlocked?.Invoke();
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            if (isRegistered)
            {
                WTSUnRegisterSessionNotification(hwnd);
                RemoveWindowSubclass(hwnd, subclassProcDelegate, SubclassId);
            }
        }
    }
}