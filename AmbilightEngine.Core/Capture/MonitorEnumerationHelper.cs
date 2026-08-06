using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AmbilightEngine.Core.Capture
{
    public sealed record MonitorInfoItem(string DeviceId, string DisplayName, IntPtr Handle);

    // Enumeruje fizycznie podłączone monitory przez natywne Win32 EnumDisplayMonitors.
    // Wcześniej RefreshMonitorsButton_Click miało puste ciało - lista ComboBoksa nigdy
    // nie była wypełniana, mimo że UI sugerowało działającą funkcję.
    public static class MonitorEnumerationHelper
    {
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public char[] szDevice;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        private const uint MONITORINFOF_PRIMARY = 0x1;

        // Zwraca listę aktualnie podłączonych monitorów. DeviceId (np. "\\.\DISPLAY1")
        // jest zapisywany w ustawieniach jako SelectedMonitorDeviceId i używany przez
        // MonitorCaptureHelper.FindMonitorHandleByDeviceName przy autostarcie przechwytywania.
        public static List<MonitorInfoItem> EnumerateMonitors()
        {
            var results = new List<MonitorInfoItem>();
            int index = 1;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                    szDevice = new char[32]
                };

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    string deviceName = new string(info.szDevice).TrimEnd('\0');
                    bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    int width = info.rcMonitor.Right - info.rcMonitor.Left;
                    int height = info.rcMonitor.Bottom - info.rcMonitor.Top;

                    string displayName = $"Monitor {index}{(isPrimary ? " (główny)" : string.Empty)} - {width}x{height}";
                    results.Add(new MonitorInfoItem(deviceName, displayName, hMonitor));
                    index++;
                }

                return true;
            }, IntPtr.Zero);

            return results;
        }
    }
}