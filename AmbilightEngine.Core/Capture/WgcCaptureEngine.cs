using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;
using WinRT;

namespace AmbilightEngine.Core.Capture
{
    public interface ICaptureSource : IDisposable
    {
        void Start(GraphicsCaptureItem targetItem);

        void Stop();

        delegate void FrameCapturedHandler(ReadOnlySpan<byte> rawPixels, int width, int height, int stride);

        event FrameCapturedHandler OnFrameCaptured;
    }

    public sealed class WgcCaptureEngine : ICaptureSource
    {
        private ID3D11Device? d3dDevice;
        private ID3D11DeviceContext? d3dContext;
        private IDirect3DDevice? winrtDevice;

        private Direct3D11CaptureFramePool? framePool;
        private GraphicsCaptureSession? captureSession;
        private ID3D11Texture2D? stagingTexture;

        private readonly object frameLock = new object();
        private bool isRunning;
        private int currentWidth;
        private int currentHeight;

        public event ICaptureSource.FrameCapturedHandler? OnFrameCaptured;

        public WgcCaptureEngine()
        {
            InitializeDirectX();
        }

        private void InitializeDirectX()
        {
            try
            {
                var featureLevels = new FeatureLevel[]
                {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0
                };

                Result result = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out ID3D11Device device,
                    out FeatureLevel obtainedLevel,
                    out ID3D11DeviceContext context);

                if (result.Failure)
                {
                    throw new InvalidOperationException("D3D11CreateDevice zwróciło błąd HRESULT: 0x" + result.Code.ToString("X"));
                }

                d3dDevice = device;
                d3dContext = context;

                winrtDevice = Direct3D11Helper.CreateDirect3DDeviceFromD3D11Device(d3dDevice);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Inicjalizacja podsystemu DirectX 11 zakończyła się krytycznym błędem.", ex);
            }
        }

        public void Start(GraphicsCaptureItem targetItem)
        {
            if (targetItem == null) throw new ArgumentNullException(nameof(targetItem));
            if (isRunning) return;

            lock (frameLock)
            {
                currentWidth = targetItem.Size.Width;
                currentHeight = targetItem.Size.Height;
                CreateStagingTexture(currentWidth, currentHeight);

                framePool = Direct3D11CaptureFramePool.Create(
                    winrtDevice!,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    targetItem.Size);

                framePool.FrameArrived += OnFrameArrived;
                captureSession = framePool.CreateCaptureSession(targetItem);
                captureSession.StartCapture();
                isRunning = true;
            }
        }

        public void Stop()
        {
            if (!isRunning) return;

            lock (frameLock)
            {
                isRunning = false;
                captureSession?.Dispose();

                if (framePool != null)
                {
                    framePool.FrameArrived -= OnFrameArrived;
                    framePool.Dispose();
                }

                captureSession = null;
                framePool = null;
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (!System.Threading.Monitor.TryEnter(frameLock)) return;

            try
            {
                using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame == null) return;

                if (frame.ContentSize.Width != currentWidth || frame.ContentSize.Height != currentHeight)
                {
                    currentWidth = frame.ContentSize.Width;
                    currentHeight = frame.ContentSize.Height;
                    CreateStagingTexture(currentWidth, currentHeight);
                    framePool?.Recreate(winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
                    return;
                }

                ProcessAndDispatchFrame(frame);
            }
            catch (Exception)
            {
                // Log w pełnej wersji. Nie ubijamy procesu przy błędzie jednej klatki.
            }
            finally
            {
                System.Threading.Monitor.Exit(frameLock);
            }
        }

        private void ProcessAndDispatchFrame(Direct3D11CaptureFrame frame)
        {
            if (d3dContext == null || stagingTexture == null) return;

            using var sourceTexture = Direct3D11Helper.GetD3D11Texture2DFromWinRTFrame(frame.Surface);

            d3dContext.CopyResource(stagingTexture, sourceTexture);

            MappedSubresource mappedResource = d3dContext.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                int stride = (int)mappedResource.RowPitch;
                int totalBytes = stride * currentHeight;

                unsafe
                {
                    var rawPixels = new ReadOnlySpan<byte>(mappedResource.DataPointer.ToPointer(), totalBytes);
                    OnFrameCaptured?.Invoke(rawPixels, currentWidth, currentHeight, stride);
                }
            }
            finally
            {
                d3dContext.Unmap(stagingTexture, 0);
            }
        }

        private void CreateStagingTexture(int width, int height)
        {
            if (d3dDevice == null) return;

            stagingTexture?.Dispose();

            var textureDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            };

            stagingTexture = d3dDevice.CreateTexture2D(textureDesc);
        }

        public void Dispose()
        {
            Stop();
            stagingTexture?.Dispose();
            d3dContext?.Dispose();
            d3dDevice?.Dispose();
            winrtDevice?.Dispose();
        }
    }

    internal static class Direct3D11Helper
    {
        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true,
            CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int CreateDirect3D11DeviceFromDXGIDeviceNative(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static IDirect3DDevice CreateDirect3DDeviceFromD3D11Device(ID3D11Device d3dDevice)
        {
            using var dxgiDevice = d3dDevice.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            int hr = CreateDirect3D11DeviceFromDXGIDeviceNative(dxgiDevice.NativePointer, out IntPtr inspectablePtr);

            if (hr != 0)
            {
                throw new Exception("Błąd CreateDirect3D11DeviceFromDXGIDevice, HRESULT: 0x" + hr.ToString("X"));
            }

            var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePtr);
            return winrtDevice;
        }

        public static ID3D11Texture2D GetD3D11Texture2DFromWinRTFrame(IDirect3DSurface surface)
        {
            var access = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
            var guid = typeof(ID3D11Texture2D).GUID;
            IntPtr ptr = access.GetInterface(ref guid);
            return new ID3D11Texture2D(ptr);
        }
    }
}