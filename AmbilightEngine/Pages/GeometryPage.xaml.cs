using System;
using System.Collections.Generic;
using System.Linq;
using AmbilightEngine.Core.Processing;
using AmbilightEngine.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AmbilightEngine.Pages
{
    public sealed partial class GeometryPage : Page
    {
        private const double PreviewWidth = 480;
        private const double PreviewHeight = 270;
        private const double ScreenMargin = 28;
        private const double LedThickness = 10;

        private MainWindow? mainWindow;
        private bool isLoadingUi;

        public GeometryPage()
        {
            InitializeComponent();
            Loaded += GeometryPage_Loaded;
        }

        private void GeometryPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;
            if (mainWindow != null)
            {
                settingsApplyService = mainWindow.SettingsApplyService;
            }

            isLoadingUi = true;

            var settings = mainWindow.Settings;

            UseCustomLayoutCheckBox.IsChecked = settings.UseCustomZoneLayout;
            SetCustomPanelEnabled(settings.UseCustomZoneLayout);

            TopCountBox.Value = settings.TopLedCount;
            BottomCountBox.Value = settings.BottomLedCount;
            LeftCountBox.Value = settings.LeftLedCount;
            RightCountBox.Value = settings.RightLedCount;

            SelectComboBoxByTag(StartCornerComboBox, settings.ZoneStartCorner.ToString());
            SelectComboBoxByTag(DirectionComboBox, settings.ZoneStripDirection.ToString());

            ShiftOffsetBox.Value = settings.ZoneShiftOffset;
            ExcludedIndicesBox.Text = string.Join(",", settings.ExcludedLedIndices);

            UpdateTotalLabel();
            RenderPreview();

            isLoadingUi = false;
        }

        private void SetCustomPanelEnabled(bool enabled)
        {
            TopCountBox.IsEnabled = enabled;
            BottomCountBox.IsEnabled = enabled;
            LeftCountBox.IsEnabled = enabled;
            RightCountBox.IsEnabled = enabled;
            StartCornerComboBox.IsEnabled = enabled;
            DirectionComboBox.IsEnabled = enabled;
            ShiftOffsetBox.IsEnabled = enabled;
            ExcludedIndicesBox.IsEnabled = enabled;
        }

        private static void SelectComboBoxByTag(ComboBox comboBox, string tagValue)
        {
            foreach (object obj in comboBox.Items)
            {
                if (obj is ComboBoxItem cbi &&
                    string.Equals(cbi.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void UpdateTotalLabel()
        {
            if (mainWindow == null)
            {
                return;
            }

            int total = mainWindow.Settings.TopLedCount
                        + mainWindow.Settings.BottomLedCount
                        + mainWindow.Settings.LeftLedCount
                        + mainWindow.Settings.RightLedCount;

            TotalCountLabel.Text = $"Łączna liczba diod w niestandardowym układzie: {total}";
        }

        private void UseCustomLayoutCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SetCustomPanelEnabled(true);

            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            mainWindow.Settings.UseCustomZoneLayout = true;
            RenderPreview();
        }

        private void UseCustomLayoutCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SetCustomPanelEnabled(false);

            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            mainWindow.Settings.UseCustomZoneLayout = false;
            RenderPreview();
        }

        private void TopCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue))
            {
                return;
            }

            mainWindow.Settings.TopLedCount = (int)args.NewValue;
            UpdateTotalLabel();
            RenderPreview();
        }

        private void BottomCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue))
            {
                return;
            }

            mainWindow.Settings.BottomLedCount = (int)args.NewValue;
            UpdateTotalLabel();
            RenderPreview();
        }

        private void LeftCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue))
            {
                return;
            }

            mainWindow.Settings.LeftLedCount = (int)args.NewValue;
            UpdateTotalLabel();
            RenderPreview();
        }

        private void RightCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue))
            {
                return;
            }

            mainWindow.Settings.RightLedCount = (int)args.NewValue;
            UpdateTotalLabel();
            RenderPreview();
        }

        private void StartCornerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            if (StartCornerComboBox.SelectedItem is ComboBoxItem cbi &&
                Enum.TryParse(cbi.Tag?.ToString(), out StartCorner parsed))
            {
                mainWindow.Settings.ZoneStartCorner = parsed;
                RenderPreview();
            }
        }

        private void DirectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            if (DirectionComboBox.SelectedItem is ComboBoxItem cbi &&
                Enum.TryParse(cbi.Tag?.ToString(), out StripDirection parsed))
            {
                mainWindow.Settings.ZoneStripDirection = parsed;
                RenderPreview();
            }
        }

        private void ShiftOffsetBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue))
            {
                return;
            }

            mainWindow.Settings.ZoneShiftOffset = (int)args.NewValue;
            RenderPreview();
        }

        private void ExcludedIndicesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            var parsedIndices = new List<int>();
            string[] parts = ExcludedIndicesBox.Text.Split(',');

            foreach (string part in parts)
            {
                if (int.TryParse(part.Trim(), out int value) && value >= 0)
                {
                    parsedIndices.Add(value);
                }
            }

            mainWindow.Settings.ExcludedLedIndices = parsedIndices.Distinct().ToList();
            RenderPreview();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null || settingsApplyService == null)
            {
                return;
            }

            settingsApplyService.SaveAndApplyGeometry(mainWindow.Settings);
        }

        // --- RENDER PREVIEW ---

        private void RenderPreview()
        {
            if (mainWindow == null)
            {
                return;
            }

            var settings = mainWindow.Settings;

            PreviewCanvas.Children.Clear();

            // Tło ekranu
            var screenRect = new Rectangle
            {
                Width = PreviewWidth - 2 * ScreenMargin,
                Height = PreviewHeight - 2 * ScreenMargin,
                Fill = new SolidColorBrush(Colors.Black),
                Stroke = new SolidColorBrush(Colors.Gray),
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4
            };

            Canvas.SetLeft(screenRect, ScreenMargin);
            Canvas.SetTop(screenRect, ScreenMargin);
            PreviewCanvas.Children.Add(screenRect);

            int top = settings.TopLedCount;
            int bottom = settings.BottomLedCount;
            int left = settings.LeftLedCount;
            int right = settings.RightLedCount;

            int total = top + bottom + left + right;
            if (total <= 0)
            {
                return;
            }

            var excluded = new HashSet<int>(settings.ExcludedLedIndices ?? Enumerable.Empty<int>());

            // Rozkład LED po obwodzie w kolejności startu i kierunku
            var positions = GenerateLedPositions(
                top,
                bottom,
                left,
                right,
                settings.ZoneStartCorner,
                settings.ZoneStripDirection,
                settings.ZoneShiftOffset);

            for (int i = 0; i < positions.Count; i++)
            {
                var pos = positions[i];

                var rect = new Rectangle
                {
                    Width = pos.Width,
                    Height = pos.Height,
                    Fill = GetLedBrush(i, total, excluded.Contains(i)),
                    Stroke = new SolidColorBrush(Colors.DimGray),
                    StrokeThickness = 0.5
                };

                Canvas.SetLeft(rect, pos.X);
                Canvas.SetTop(rect, pos.Y);

                PreviewCanvas.Children.Add(rect);
            }
        }

        private SolidColorBrush GetLedBrush(int index, int total, bool isExcluded)
        {
            if (isExcluded)
            {
                return new SolidColorBrush(Colors.DarkSlateGray);
            }

            if (total <= 1)
            {
                return new SolidColorBrush(Colors.DeepSkyBlue);
            }

            double t = (double)index / (total - 1);

            byte r = (byte)(255 * t);
            byte g = (byte)(128 * (1.0 - t));
            byte b = (byte)(255 * (1.0 - t));

            return new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }

        private sealed class LedPosition
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        private List<LedPosition> GenerateLedPositions(
            int top,
            int bottom,
            int left,
            int right,
            StartCorner startCorner,
            StripDirection direction,
            int shiftOffset)
        {
            var result = new List<LedPosition>();

            double leftX = ScreenMargin;
            double rightX = PreviewWidth - ScreenMargin;
            double topY = ScreenMargin;
            double bottomY = PreviewHeight - ScreenMargin;

            double innerWidth = PreviewWidth - 2 * ScreenMargin;
            double innerHeight = PreviewHeight - 2 * ScreenMargin;

            // TOP (left -> right)
            for (int i = 0; i < top; i++)
            {
                double x = leftX + innerWidth * ((i + 0.5) / top);
                result.Add(new LedPosition
                {
                    X = x - LedThickness / 2,
                    Y = topY - LedThickness / 2,
                    Width = LedThickness,
                    Height = LedThickness
                });
            }

            // RIGHT (top -> bottom)
            for (int i = 0; i < right; i++)
            {
                double y = topY + innerHeight * ((i + 0.5) / right);
                result.Add(new LedPosition
                {
                    X = rightX - LedThickness / 2,
                    Y = y - LedThickness / 2,
                    Width = LedThickness,
                    Height = LedThickness
                });
            }

            // BOTTOM (right -> left)
            for (int i = 0; i < bottom; i++)
            {
                double x = rightX - innerWidth * ((i + 0.5) / bottom);
                result.Add(new LedPosition
                {
                    X = x - LedThickness / 2,
                    Y = bottomY - LedThickness / 2,
                    Width = LedThickness,
                    Height = LedThickness
                });
            }

            // LEFT (bottom -> top)
            for (int i = 0; i < left; i++)
            {
                double y = bottomY - innerHeight * ((i + 0.5) / left);
                result.Add(new LedPosition
                {
                    X = leftX - LedThickness / 2,
                    Y = y - LedThickness / 2,
                    Width = LedThickness,
                    Height = LedThickness
                });
            }

            // Dopasowanie kolejności do StartCorner + StripDirection
            int total = result.Count;
            if (total == 0)
            {
                return result;
            }

            int startIndex = startCorner switch
            {
                StartCorner.TopLeft => 0,
                StartCorner.TopRight => top + right - 1,
                StartCorner.BottomRight => top + right + bottom - 1,
                StartCorner.BottomLeft => total - 1,
                _ => 0
            };

            var reordered = new List<LedPosition>(total);

            if (direction == StripDirection.Clockwise)
            {
                // Idziemy w przód od startIndex
                for (int i = 0; i < total; i++)
                {
                    int idx = (startIndex + i) % total;
                    reordered.Add(result[idx]);
                }
            }
            else
            {
                // CounterClockwise: idziemy w tył od startIndex
                for (int i = 0; i < total; i++)
                {
                    int idx = (startIndex - i) % total;
                    if (idx < 0) idx += total;
                    reordered.Add(result[idx]);
                }
            }

            // ZoneShiftOffset
            if (shiftOffset != 0)
            {
                int offset = shiftOffset % total;
                if (offset < 0) offset += total;

                var shifted = new List<LedPosition>(total);
                for (int i = 0; i < total; i++)
                {
                    int idx = (i + offset) % total;
                    shifted.Add(reordered[idx]);
                }

                reordered = shifted;
            }

            return reordered;
        }
        private ISettingsApplyService? settingsApplyService;
    }
}