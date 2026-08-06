using System;
using System.Collections.Generic;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Core.SystemState;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AmbilightEngine.Controls
{
    // Wizualizuje aktualny stan diod LED na kształcie przypominającym monitor.
    // Podgląd kolorów pochodzi z funkcji "Peek" WLED przez WebSocket, współdzielony
    // przez WledLivePreviewHub - WLED serwuje strumień Peek tylko jednemu klientowi
    // naraz, więc wszystkie instancje tej kontrolki (Dashboard, Ekran blokady,
    // Bezczynność) muszą korzystać z JEDNEGO fizycznego połączenia per adres IP.
    //
    // WAŻNE: Configure() startuje subskrypcję bezpośrednio, NIE z Loaded - elementy
    // wewnątrz paneli z Visibility="Collapsed" nie odpalają zdarzenia Loaded w WinUI 3.
    public sealed partial class LedStripPreviewControl : UserControl
    {
        private const int NominalScreenWidth = 1920;
        private const int NominalScreenHeight = 1080;

        private const double CanvasScreenRectX = 60;
        private const double CanvasScreenRectY = 60;
        private const double CanvasScreenRectWidth = 580;
        private const double CanvasScreenRectHeight = 326;
        private const double SwatchSize = 18;
        private const double SwatchOffset = 14;

        private readonly DispatcherQueue dispatcherQueue;
        private readonly List<Rectangle> ledSwatches = new();

        private IDisposable? hubSubscription;
        private string? lastConfiguredIp;
        private bool hasLoggedCountMismatch;

        public LedStripPreviewControl()
        {
            InitializeComponent();
            dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            Unloaded += (_, _) =>
            {
                hubSubscription?.Dispose();
                hubSubscription = null;
            };
        }

        // Przebudowuje geometrię wizualizacji i (re)łączy podgląd na żywo z aktualnym
        // adresem IP WLED przez współdzielony Hub. Wywołaj po każdej zmianie ustawień
        // geometrii lub adresu IP. Działa natychmiast, niezależnie od stanu Visibility/Loaded.
        public void Configure(AmbilightSettings settings)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DIAG] LedStripPreviewControl.Configure wywołane, IP={settings.EspIpAddress}");

            RebuildGeometry(settings);

            if (!string.Equals(lastConfiguredIp, settings.EspIpAddress, StringComparison.OrdinalIgnoreCase))
            {
                hubSubscription?.Dispose();
                lastConfiguredIp = settings.EspIpAddress;
                hubSubscription = WledLivePreviewHub.Instance.Subscribe(
                    settings.EspIpAddress, OnLiveColorsReceived, OnConnectionStateChanged);
            }
        }

        private void RebuildGeometry(AmbilightSettings settings)
        {
            PreviewCanvas.Children.Clear();
            ledSwatches.Clear();

            DrawScreenOutline();

            CaptureZone[] zones = BuildNominalZones(settings);

            System.Diagnostics.Debug.WriteLine(
                $"[DIAG] LedStripPreviewControl: zbudowano geometrię, liczba stref={zones.Length}");

            foreach (CaptureZone zone in zones)
            {
                var swatch = new Rectangle
                {
                    Width = SwatchSize,
                    Height = SwatchSize,
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                    StrokeThickness = 1
                };

                (double centerX, double centerY) = ComputeSwatchPosition(zone, settings.SamplingDepth);

                Canvas.SetLeft(swatch, centerX - SwatchSize / 2);
                Canvas.SetTop(swatch, centerY - SwatchSize / 2);

                PreviewCanvas.Children.Add(swatch);
                ledSwatches.Add(swatch);
            }
        }

        private void DrawScreenOutline()
        {
            var screenRect = new Rectangle
            {
                Width = CanvasScreenRectWidth,
                Height = CanvasScreenRectHeight,
                RadiusX = 6,
                RadiusY = 6,
                Fill = new SolidColorBrush(Color.FromArgb(255, 12, 12, 16)),
                Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                StrokeThickness = 2
            };

            Canvas.SetLeft(screenRect, CanvasScreenRectX);
            Canvas.SetTop(screenRect, CanvasScreenRectY);
            PreviewCanvas.Children.Add(screenRect);
        }

        private static CaptureZone[] BuildNominalZones(AmbilightSettings settings)
        {
            if (settings.UseCustomZoneLayout)
            {
                return ZoneMapGenerator.Generate(
                    NominalScreenWidth, NominalScreenHeight,
                    settings.TopLedCount, settings.BottomLedCount,
                    settings.LeftLedCount, settings.RightLedCount,
                    settings.SamplingDepth,
                    settings.ZoneStartCorner, settings.ZoneStripDirection,
                    settings.ZoneShiftOffset, settings.ExcludedLedIndices);
            }

            return ZoneMapGenerator.Generate(
                NominalScreenWidth, NominalScreenHeight, settings.LedCount, settings.SamplingDepth);
        }

        private static (double x, double y) ComputeSwatchPosition(CaptureZone zone, int samplingDepth)
        {
            bool isHorizontalEdge = zone.Height == samplingDepth;
            bool isVerticalEdge = zone.Width == samplingDepth;

            if (isHorizontalEdge)
            {
                double normalizedX = (zone.X + zone.Width / 2.0) / NominalScreenWidth;
                double canvasX = CanvasScreenRectX + normalizedX * CanvasScreenRectWidth;
                bool isTop = zone.Y == 0;
                double canvasY = isTop
                    ? CanvasScreenRectY - SwatchOffset
                    : CanvasScreenRectY + CanvasScreenRectHeight + SwatchOffset;

                return (canvasX, canvasY);
            }

            if (isVerticalEdge)
            {
                double normalizedY = (zone.Y + zone.Height / 2.0) / NominalScreenHeight;
                double canvasY = CanvasScreenRectY + normalizedY * CanvasScreenRectHeight;
                bool isLeft = zone.X == 0;
                double canvasX = isLeft
                    ? CanvasScreenRectX - SwatchOffset
                    : CanvasScreenRectX + CanvasScreenRectWidth + SwatchOffset;

                return (canvasX, canvasY);
            }

            return (-100, -100);
        }

        private void OnLiveColorsReceived(RgbColor[] colors)
        {
            dispatcherQueue.TryEnqueue(() =>
            {
                if (!hasLoggedCountMismatch && colors.Length != ledSwatches.Count)
                {
                    hasLoggedCountMismatch = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[DIAG] LedStripPreview: NIEZGODNOŚĆ liczby diod - WLED zwraca {colors.Length}, wizualizacja ma {ledSwatches.Count} kwadracików.");
                }

                int count = Math.Min(colors.Length, ledSwatches.Count);

                for (int i = 0; i < count; i++)
                {
                    RgbColor c = colors[i];
                    ledSwatches[i].Fill = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
                }
            });
        }

        private void OnConnectionStateChanged(bool isConnected)
        {
            dispatcherQueue.TryEnqueue(() =>
            {
                ConnectionStatusDot.Fill = new SolidColorBrush(
                    isConnected ? Color.FromArgb(255, 60, 200, 90) : Color.FromArgb(255, 200, 60, 60));

                ConnectionStatusText.Text = isConnected
                    ? "Podgląd na żywo: połączono"
                    : "Podgląd na żywo: łączenie z WLED...";
            });
        }
    }
}