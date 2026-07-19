using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace AmbilightEngine.Core.Capture
{
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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

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