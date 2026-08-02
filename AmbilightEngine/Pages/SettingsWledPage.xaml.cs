using System;
using AmbilightEngine.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsWledPage : Page
    {
        private IWledDiagnosticsService? wledDiagnosticsService;
        private MainWindow? mainWindow;

        public SettingsWledPage()
        {
            InitializeComponent();
            Loaded += SettingsWledPage_Loaded;
        }

        private void SettingsWledPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;
            if (mainWindow == null)
            {
                return;
            }

            wledDiagnosticsService = mainWindow.WledDiagnosticsService;
        }

        private void IpAddressBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            WledDiagnosticStatusText.Text = "Brak danych";
            WledDeviceNameText.Text = "-";
            WledVersionText.Text = "-";
            WledPowerStateText.Text = "-";
            WledBrightnessText.Text = "-";
            WledLedMatchText.Text = "-";
        }

        private async void TestWledConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (wledDiagnosticsService == null)
            {
                return;
            }

            string ip = IpAddressBox.Text?.Trim() ?? string.Empty;
            bool ok = await wledDiagnosticsService.TestConnectionAsync(ip);

            WledDiagnosticStatusText.Text = ok ? "Połączono" : "Brak połączenia";
        }

        private void LedCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
        }

       

        private async void RefreshWledDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            if (wledDiagnosticsService == null)
            {
                return;
            }

            string ip = IpAddressBox.Text?.Trim() ?? string.Empty;
            WledDiagnosticsResult diag = await wledDiagnosticsService.GetDiagnosticsAsync(ip);

            WledDiagnosticStatusText.Text = diag.StatusText;
            WledDeviceNameText.Text = diag.DeviceName;
            WledVersionText.Text = diag.Version;
            WledPowerStateText.Text = diag.PowerState;
            WledBrightnessText.Text = diag.Brightness;
            WledLedMatchText.Text = diag.LedInfo;
        }
    }
}