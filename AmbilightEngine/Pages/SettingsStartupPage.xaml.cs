using System;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.SystemState;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsStartupPage : Page
    {
        private MainWindow? mainWindow;
        private bool isLoadingUi = true;

        // Debounce dla żądań efektu ambientowego, żeby przeciąganie sliderów nie zalewało
        // WLED requestami HTTP - identyczny mechanizm jak w DashboardPage.
        private DispatcherQueueTimer? lockScreenDebounceTimer;
        private DispatcherQueueTimer? idleDebounceTimer;
        private CancellationTokenSource? lockScreenApplyCts;
        private CancellationTokenSource? idleApplyCts;
        private static readonly TimeSpan EffectDebounceDelay = TimeSpan.FromMilliseconds(150);

        public SettingsStartupPage()
        {
            InitializeComponent();
            Loaded += SettingsStartupPage_Loaded;
            Unloaded += SettingsStartupPage_Unloaded;
        }

        private async void SettingsStartupPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = Application.Current is App { MainAppWindow: not null } app
                ? app.MainAppWindow
                : null;

            if (mainWindow is null) return;

            isLoadingUi = true;

            var settings = mainWindow.Settings;

            StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
            StartMinimizedToTrayCheckBox.IsChecked = settings.StartMinimizedToTray;
            CloseToTrayCheckBox.IsChecked = settings.CloseToTray;
            AutoMonitorCheckBox.IsChecked = settings.AutoStartWithDefaultMonitor;

            IdleTimeoutSlider.Value = settings.IdleTimeoutMinutes;
            IdleTimeoutValueText.Text = settings.IdleTimeoutMinutes.ToString();

            await LoadEffectsAndPalettesAsync();

            LoadAmbientConfigIntoUi(settings.LockScreenAmbient, LockScreenAmbientToggle, LockScreenAmbientPanel,
                LockScreenEffectComboBox, LockScreenPaletteComboBox,
                LockScreenSpeedSlider, LockScreenSpeedValueText,
                LockScreenIntensitySlider, LockScreenIntensityValueText,
                LockScreenBrightnessSlider, LockScreenBrightnessValueText,
                LockScreenPrimaryColorPicker, LockScreenSecondaryColorPicker);

            LoadAmbientConfigIntoUi(settings.IdleAmbient, IdleAmbientToggle, IdleAmbientPanel,
                IdleEffectComboBox, IdlePaletteComboBox,
                IdleSpeedSlider, IdleSpeedValueText,
                IdleIntensitySlider, IdleIntensityValueText,
                IdleBrightnessSlider, IdleBrightnessValueText,
                IdlePrimaryColorPicker, IdleSecondaryColorPicker);

            isLoadingUi = false;
            LockScreenPreviewControl.Configure(settings);
            IdlePreviewControl.Configure(settings);
        }

        private void SettingsStartupPage_Unloaded(object sender, RoutedEventArgs e)
        {
            lockScreenDebounceTimer?.Stop();
            idleDebounceTimer?.Stop();
            lockScreenApplyCts?.Cancel();
            idleApplyCts?.Cancel();
        }

        private async Task LoadEffectsAndPalettesAsync()
        {
            if (mainWindow == null) return;

            try
            {
                var effects = await mainWindow.EngineHost.GetAvailableWledEffectsAsync();
                var palettes = await mainWindow.EngineHost.GetAvailableWledPalettesAsync();

                foreach (string effectName in effects)
                {
                    LockScreenEffectComboBox.Items.Add(effectName);
                    IdleEffectComboBox.Items.Add(effectName);
                }

                foreach (string paletteName in palettes)
                {
                    LockScreenPaletteComboBox.Items.Add(paletteName);
                    IdlePaletteComboBox.Items.Add(paletteName);
                }
            }
            catch (Exception)
            {
                // Brak połączenia z WLED przy otwarciu strony nie może zablokować UI -
                // listy zostaną puste, użytkownik może odświeżyć po nawiązaniu połączenia.
            }
        }

        private static void LoadAmbientConfigIntoUi(
            AmbientEffectConfig config,
            ToggleSwitch toggle, StackPanel panel,
            ComboBox effectCombo, ComboBox paletteCombo,
            Slider speedSlider, TextBlock speedText,
            Slider intensitySlider, TextBlock intensityText,
            Slider brightnessSlider, TextBlock brightnessText,
            ColorPicker primaryPicker, ColorPicker secondaryPicker)
        {
            toggle.IsOn = config.IsEnabled;
            panel.Visibility = config.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            panel.Opacity = config.IsEnabled ? 1.0 : 0.5;
            panel.IsHitTestVisible = config.IsEnabled;

            if (config.EffectId < effectCombo.Items.Count)
            {
                effectCombo.SelectedIndex = config.EffectId;
            }

            if (config.PaletteId < paletteCombo.Items.Count)
            {
                paletteCombo.SelectedIndex = config.PaletteId;
            }

            speedSlider.Value = config.Speed;
            speedText.Text = config.Speed.ToString();

            intensitySlider.Value = config.Intensity;
            intensityText.Text = config.Intensity.ToString();

            brightnessSlider.Value = config.Brightness;
            brightnessText.Text = config.Brightness.ToString();

            primaryPicker.Color = Color.FromArgb(255, config.PrimaryColorR, config.PrimaryColorG, config.PrimaryColorB);
            secondaryPicker.Color = Color.FromArgb(255, config.SecondaryColorR, config.SecondaryColorG, config.SecondaryColorB);
        }

        // ── Karta "Autostart i zasobnik" (bez zmian funkcjonalnych - poza zakresem tej zmiany) ──

        private void StartWithWindowsCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.StartWithWindows = true;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void StartWithWindowsCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.StartWithWindows = false;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void StartMinimizedToTrayCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.StartMinimizedToTray = true;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void StartMinimizedToTrayCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.StartMinimizedToTray = false;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void CloseToTrayCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.CloseToTray = true;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void CloseToTrayCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.CloseToTray = false;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void AutoStartAmbilightCheckBox_Checked(object sender, RoutedEventArgs e)
        {
        }

        private void AutoStartAmbilightCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
        }

        private void AutoMonitorCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.AutoStartWithDefaultMonitor = true;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void AutoMonitorCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.AutoStartWithDefaultMonitor = false;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void RefreshMonitorsButton_Click(object sender, RoutedEventArgs e)
        {
        }

        // ── Karta "Tryb ambientowy" - timeout bezczynności ──

        private void IdleTimeoutSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleTimeoutValueText.Text = ((int)e.NewValue).ToString();

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleTimeoutMinutes = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        // ── Karta "Ekran blokady" ──

        private void LockScreenAmbientToggle_Toggled(object sender, RoutedEventArgs e)
        {
            LockScreenAmbientPanel.Visibility = LockScreenAmbientToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            LockScreenAmbientPanel.Opacity = LockScreenAmbientToggle.IsOn ? 1.0 : 0.5;
            LockScreenAmbientPanel.IsHitTestVisible = LockScreenAmbientToggle.IsOn;

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.IsEnabled = LockScreenAmbientToggle.IsOn;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenEffectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || LockScreenEffectComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.LockScreenAmbient.EffectId = LockScreenEffectComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenPaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || LockScreenPaletteComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.LockScreenAmbient.PaletteId = LockScreenPaletteComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            LockScreenSpeedValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.Speed = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenIntensitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            LockScreenIntensityValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.Intensity = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            LockScreenBrightnessValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.Brightness = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenPrimaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.PrimaryColorR = args.NewColor.R;
            mainWindow.Settings.LockScreenAmbient.PrimaryColorG = args.NewColor.G;
            mainWindow.Settings.LockScreenAmbient.PrimaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void LockScreenSecondaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.SecondaryColorR = args.NewColor.R;
            mainWindow.Settings.LockScreenAmbient.SecondaryColorG = args.NewColor.G;
            mainWindow.Settings.LockScreenAmbient.SecondaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        // ── Karta "Bezczynność" ──

        private void IdleAmbientToggle_Toggled(object sender, RoutedEventArgs e)
        {
            IdleAmbientPanel.Visibility = IdleAmbientToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            IdleAmbientPanel.Opacity = IdleAmbientToggle.IsOn ? 1.0 : 0.5;
            IdleAmbientPanel.IsHitTestVisible = IdleAmbientToggle.IsOn;

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.IsEnabled = IdleAmbientToggle.IsOn;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdleEffectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || IdleEffectComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.IdleAmbient.EffectId = IdleEffectComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdlePaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || IdlePaletteComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.IdleAmbient.PaletteId = IdlePaletteComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdleSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleSpeedValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.Speed = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdleIntensitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleIntensityValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.Intensity = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdleBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleBrightnessValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.Brightness = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdlePrimaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.PrimaryColorR = args.NewColor.R;
            mainWindow.Settings.IdleAmbient.PrimaryColorG = args.NewColor.G;
            mainWindow.Settings.IdleAmbient.PrimaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void IdleSecondaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.SecondaryColorR = args.NewColor.R;
            mainWindow.Settings.IdleAmbient.SecondaryColorG = args.NewColor.G;
            mainWindow.Settings.IdleAmbient.SecondaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }
    }
}