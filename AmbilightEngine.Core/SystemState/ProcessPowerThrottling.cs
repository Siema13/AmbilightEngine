using System;
using System.Runtime.InteropServices;

namespace AmbilightEngine.Core.SystemState
{
    // Windows domyślnie stosuje throttling (Efficiency Mode / EcoQoS) do procesów bez
    // widocznego, aktywnego okna - zablokowany ekran jest traktowany podobnie jak
    // zminimalizowana aplikacja w tle. To drastycznie spowalnia lub zamraża callbacki
    // System.Threading.Timer, co uniemożliwia działanie SystemStateWatcher podczas blokady.
    // Ta klasa jawnie wyłącza throttling dla całego procesu przy starcie aplikacji.
    public static class ProcessPowerThrottling
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        // Wartość 4 = ProcessPowerThrottling w enumeracji PROCESS_INFORMATION_CLASS (Win32).
        private const int ProcessPowerThrottlingInformationClass = 4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation,
            uint processInformationSize);

        // Wywołaj to raz, jak najwcześniej przy starcie aplikacji (np. w konstruktorze App).
        // Zwraca true, jeśli udało się wyłączyć throttling.
        public static bool DisableThrottling()
        {
            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0 // 0 = throttling WYŁĄCZONY dla flagi ControlMask powyżej.
                };

                uint size = (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
                IntPtr currentProcess = GetCurrentProcess();

                bool success = SetProcessInformation(
                    currentProcess, ProcessPowerThrottlingInformationClass, ref state, size);

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] ProcessPowerThrottling.DisableThrottling: sukces={success}");

                return success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] ProcessPowerThrottling.DisableThrottling: błąd - {ex.Message}");
                return false;
            }
        }
    }
}