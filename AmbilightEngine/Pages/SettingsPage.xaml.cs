using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private MainWindow? mainWindow;
        private bool isLoadingUi;

        public SettingsPage()
        {
            InitializeComponent();
            this.Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;
            if (mainWindow == null) return;

            isLoadingUi = true;
            var settings = mainWindow.Settings;

            AutoMonitorCheckBox.IsChecked = settings.AutoStartWithDefaultMonitor;
            IpAddressBox.Text = settings.EspIpAddress;
            LedCountBox.Value = settings.LedCount;
            SamplingDepthSlider.Value = settings.SamplingDepth;
            SmoothingSlider.Value = settings.SmoothingFactor * 100;
            QualitySlider.Value = settings.PixelSkipStep;
            IdleTimeoutBox.Value = settings.IdleTimeoutMinutes;
            LoungeColorPicker.Color = Color.FromArgb(255, settings.LoungeColorR, settings.LoungeColorG, settings.LoungeColorB);

            isLoadingUi = false;
        }

        private void AutoMonitorCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.AutoStartWithDefaultMonitor = true;
        }

        private void AutoMonitorCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.AutoStartWithDefaultMonitor = false;
        }

        private void IpAddressBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.EspIpAddress = IpAddressBox.Text;
        }

        private void LedCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue)) return;
            mainWindow.Settings.LedCount = (int)args.NewValue;
        }

        private void SamplingDepthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.SamplingDepth = (int)e.NewValue;
        }

        private void SmoothingSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.SmoothingFactor = (float)(e.NewValue / 100.0);
            mainWindow.EngineHost.ApplyLiveSettings();
        }

        private void QualitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.PixelSkipStep = (int)e.NewValue;
            mainWindow.EngineHost.ApplyLiveSettings();
        }

        private void IdleTimeoutBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null || double.IsNaN(args.NewValue)) return;
            mainWindow.Settings.IdleTimeoutMinutes = (int)args.NewValue;
        }

        private void LoungeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LoungeColorR = args.NewColor.R;
            mainWindow.Settings.LoungeColorG = args.NewColor.G;
            mainWindow.Settings.LoungeColorB = args.NewColor.B;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null) return;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }
    }
}