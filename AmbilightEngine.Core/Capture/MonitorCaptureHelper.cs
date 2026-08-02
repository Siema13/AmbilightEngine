using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace AmbilightEngine.Core.Capture
{
    // Opisuje jeden fizyczny monitor wykryty w systemie - używane do budowy listy wyboru w UI.
    // DeviceName (np. "\\.\DISPLAY1") jest stabilnym identyfikatorem do zapisu w ustawieniach,
    // w przeciwieństwie do samego uchwytu HMONITOR, który może się zmienić między sesjami.
    public sealed class MonitorDescriptor
    {
        public IntPtr Handle { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }

        public string DisplayLabel => $"{DeviceName} ({Width}x{Height}){(IsPrimary ? " - główny" : string.Empty)}";
    }

    public static class MonitorCaptureHelper
    {
        private const int MONITOR_DEFAULTTOPRIMARY = 1;
        private const string GraphicsCaptureItemRuntimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

        private static readonly Guid IGraphicsCaptureItemInteropGuid =
            new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        private static readonly Guid IGraphicsCaptureItemGuid =
            new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private const uint MONITORINFOF_PRIMARY = 1;

        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            out IntPtr factory);

        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(IntPtr window, ref Guid iid);
            IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
        }

        public static IntPtr GetPrimaryMonitorHandle(IntPtr appWindowHandle)
        {
            if (appWindowHandle != IntPtr.Zero)
            {
                IntPtr monitorFromAppWindow = MonitorFromWindow(appWindowHandle, MONITOR_DEFAULTTOPRIMARY);
                if (monitorFromAppWindow != IntPtr.Zero)
                {
                    return monitorFromAppWindow;
                }
            }

            POINT originPoint = new POINT();
            originPoint.X = 0;
            originPoint.Y = 0;

            IntPtr fallbackMonitor = MonitorFromPoint(originPoint, MONITOR_DEFAULTTOPRIMARY);

            if (fallbackMonitor == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Nie udalo sie uzyskac uchwytu glownego monitora systemowego (MonitorFromPoint zwrocilo null).");
            }

            return fallbackMonitor;
        }

        // Zwraca listę wszystkich fizycznie podłączonych monitorów - używane do zbudowania
        // ComBox-a wyboru w Ustawieniach. Kolejność nie jest gwarantowana przez system,
        // więc UI powinno sortować/prezentować wg DeviceName, jeśli potrzebna jest stabilność wizualna.
        public static List<MonitorDescriptor> GetAllMonitors()
        {
            var monitors = new List<MonitorDescriptor>();

            bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    monitors.Add(new MonitorDescriptor
                    {
                        Handle = hMonitor,
                        DeviceName = info.szDevice,
                        Width = info.rcMonitor.Right - info.rcMonitor.Left,
                        Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
                    });
                }

                return true;
            }

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
            return monitors;
        }

        // Odnajduje aktualny uchwyt HMONITOR odpowiadający zapisanemu wcześniej DeviceName.
        // Konieczne, bo same uchwyty HMONITOR nie są stabilne między sesjami systemu (np. po
        // ponownym uruchomieniu komputera lub zmianie konfiguracji ekranów), dlatego w ustawieniach
        // zapisujemy tylko nazwę urządzenia, a przy każdym starcie odnajdujemy aktualny uchwyt na nowo.
        public static IntPtr FindMonitorHandleByDeviceName(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return IntPtr.Zero;

            foreach (var monitor in GetAllMonitors())
            {
                if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return monitor.Handle;
                }
            }

            return IntPtr.Zero;
        }

        public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
        {
            if (hmonitor == IntPtr.Zero)
            {
                throw new ArgumentException("Uchwyt monitora (HMONITOR) nie moze byc zerowy.", "hmonitor");
            }

            IntPtr hClassName = IntPtr.Zero;
            IntPtr factoryPtr = IntPtr.Zero;
            IntPtr itemPtr = IntPtr.Zero;

            try
            {
                int hrString = WindowsCreateString(
                    GraphicsCaptureItemRuntimeClassName,
                    GraphicsCaptureItemRuntimeClassName.Length,
                    out hClassName);

                if (hrString != 0)
                {
                    throw new InvalidOperationException(
                        "WindowsCreateString nie powiodlo sie (HRESULT " + hrString.ToString("X8") + ").");
                }

                Guid interopGuid = IGraphicsCaptureItemInteropGuid;
                int hrFactory = RoGetActivationFactory(hClassName, ref interopGuid, out factoryPtr);

                if (hrFactory != 0 || factoryPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "RoGetActivationFactory nie powiodlo sie (HRESULT " + hrFactory.ToString("X8") + ").");
                }

                IGraphicsCaptureItemInterop interopFactory =
                    (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                        factoryPtr, typeof(IGraphicsCaptureItemInterop));

                Guid itemGuid = IGraphicsCaptureItemGuid;
                itemPtr = interopFactory.CreateForMonitor(hmonitor, ref itemGuid);

                if (itemPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "CreateForMonitor zwrocilo null wskaznik do GraphicsCaptureItem.");
                }

                GraphicsCaptureItem item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                return item;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Nieprzewidziany blad podczas tworzenia GraphicsCaptureItem z uchwytu monitora.", ex);
            }
            finally
            {
                if (itemPtr != IntPtr.Zero)
                {
                    Marshal.Release(itemPtr);
                }

                if (factoryPtr != IntPtr.Zero)
                {
                    Marshal.Release(factoryPtr);
                }

                if (hClassName != IntPtr.Zero)
                {
                    WindowsDeleteString(hClassName);
                }
            }
        }
    }
}