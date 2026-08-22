using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Capture;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Models;
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

        // Debounce dla żądań efektu/presetu ambientowego, żeby przeciąganie sliderów
        // nie zalewało WLED requestami HTTP - identyczny mechanizm jak w DashboardPage.
        private DispatcherQueueTimer? lockScreenDebounceTimer;
        private DispatcherQueueTimer? idleDebounceTimer;
        private CancellationTokenSource? lockScreenApplyCts;
        private CancellationTokenSource? idleApplyCts;
        private static readonly TimeSpan EffectDebounceDelay = TimeSpan.FromMilliseconds(150);

        private bool hasSentAnyPreview;

        private List<MonitorInfoItem> loadedMonitors = new();

        // NOWOŚĆ: lista presetów WLED współdzielona przez obie karty (Ekran blokady
        // i Bezczynność) - wczytywana automatycznie przy Loaded, bez konieczności
        // ręcznego klikania. Przycisk "Odśwież presety WLED" pozwala odświeżyć ją
        // ręcznie po dodaniu nowego presetu bezpośrednio w aplikacji webowej WLED.
        private readonly WledPresetService presetService = new();

        public ObservableCollection<WledPresetInfo> LoadedPresets { get; } = new();

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
            AutoStartAmbilightCheckBox.IsChecked = settings.AutoStartAmbilight;
            AutoStartDisplayModeComboBox.SelectedIndex = settings.AutoStartDisplayMode switch
            {
                DisplayMode.StaticColor => 1,
                DisplayMode.WledEffects => 2,
                _ => 0
            };

            AutoStartDisplayModeComboBox.IsEnabled = settings.AutoStartAmbilight;

            RefreshMonitorsList();

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

            LockScreenModePresetRadio.IsChecked = settings.LockScreenAmbient.UsePreset;
            LockScreenModeEffectRadio.IsChecked = !settings.LockScreenAmbient.UsePreset;
            LockScreenPresetPanel.Visibility = settings.LockScreenAmbient.UsePreset ? Visibility.Visible : Visibility.Collapsed;
            LockScreenEffectPanel.Visibility = settings.LockScreenAmbient.UsePreset ? Visibility.Collapsed : Visibility.Visible;

            IdleModePresetRadio.IsChecked = settings.IdleAmbient.UsePreset;
            IdleModeEffectRadio.IsChecked = !settings.IdleAmbient.UsePreset;
            IdlePresetPanel.Visibility = settings.IdleAmbient.UsePreset ? Visibility.Visible : Visibility.Collapsed;
            IdleEffectPanel.Visibility = settings.IdleAmbient.UsePreset ? Visibility.Collapsed : Visibility.Visible;

            // NOWOŚĆ: automatyczne wczytanie listy presetów WLED, PO wypełnieniu list
            // efektów/palet, żeby oba ComboBoxy (efekt i preset) były gotowe od razu
            // przy pierwszym wejściu na stronę.
            await RefreshPresetsAsync();

            // Ustawienie zaznaczenia presetu w ComboBox dopiero PO wczytaniu listy -
            // wcześniej lista byłaby pusta i SelectedValue nie miałby czego wybrać.
            SetSelectedPreset(LockScreenPresetComboBox, settings.LockScreenAmbient.PresetId);
            SetSelectedPreset(IdlePresetComboBox, settings.IdleAmbient.PresetId);

            isLoadingUi = false;
            LockScreenPreviewControl.Configure(settings);
            IdlePreviewControl.Configure(settings);
        }

        private async Task RefreshPresetsAsync()
        {
            if (mainWindow == null)
            {
                return;
            }

            try
            {
                List<WledPresetInfo> presets = await presetService.GetPresetsAsync(mainWindow.Settings.EspIpAddress);

                LoadedPresets.Clear();
                foreach (WledPresetInfo preset in presets)
                {
                    LoadedPresets.Add(preset);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] SettingsStartupPage: wczytano {LoadedPresets.Count} presetów WLED.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DIAG] SettingsStartupPage: błąd wczytywania presetów WLED: {ex.Message}");
            }
        }

        private async void RefreshPresetsButton_Click(object sender, RoutedEventArgs e)
        {
            int previousLockScreenPresetId = mainWindow?.Settings.LockScreenAmbient.PresetId ?? 0;
            int previousIdlePresetId = mainWindow?.Settings.IdleAmbient.PresetId ?? 0;

            await RefreshPresetsAsync();

            // Po ręcznym odświeżeniu przywracamy poprzednio wybrany numer presetu
            // w obu ComboBoxach, jeśli nadal istnieje na wczytanej liście.
            SetSelectedPreset(LockScreenPresetComboBox, previousLockScreenPresetId);
            SetSelectedPreset(IdlePresetComboBox, previousIdlePresetId);
        }

        private static void SetSelectedPreset(ComboBox comboBox, int presetId)
        {
            if (presetId <= 0)
            {
                return;
            }

            foreach (object item in comboBox.Items)
            {
                if (item is WledPresetInfo preset && preset.PresetId == presetId)
                {
                    comboBox.SelectedItem = preset;
                    return;
                }
            }
        }

        private void SettingsStartupPage_Unloaded(object sender, RoutedEventArgs e)
        {
            lockScreenDebounceTimer?.Stop();
            idleDebounceTimer?.Stop();
            lockScreenApplyCts?.Cancel();
            idleApplyCts?.Cancel();

            if (hasSentAnyPreview && mainWindow != null &&
                mainWindow.Settings.ActiveDisplayMode == DisplayMode.WledEffects)
            {
                var settings = mainWindow.Settings;
                var primary = (settings.LastWledPrimaryColorR, settings.LastWledPrimaryColorG, settings.LastWledPrimaryColorB);
                var secondary = (settings.LastWledSecondaryColorR, settings.LastWledSecondaryColorG, settings.LastWledSecondaryColorB);

                _ = mainWindow.EngineHost.PreviewWledEffectAsync(
                    settings.LastWledEffectId, settings.LastWledSpeed, settings.LastWledIntensity,
                    settings.LastWledPaletteId, primary, secondary, settings.LastWledBrightness,
                    cancellationToken: CancellationToken.None);
            }
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

        // ── Podgląd na żywo dla Ekranu blokady ──────────────────────────────────

        private void RequestLockScreenPreviewDebounced()
        {
            if (mainWindow == null) return;

            lockScreenDebounceTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
            lockScreenDebounceTimer.Stop();

            lockScreenApplyCts?.Cancel();
            lockScreenApplyCts?.Dispose();
            lockScreenApplyCts = new CancellationTokenSource();

            lockScreenDebounceTimer.Interval = EffectDebounceDelay;
            lockScreenDebounceTimer.IsRepeating = false;
            lockScreenDebounceTimer.Tick -= OnLockScreenDebounceTick;
            lockScreenDebounceTimer.Tick += OnLockScreenDebounceTick;
            lockScreenDebounceTimer.Start();
        }

        private void OnLockScreenDebounceTick(object? sender, object e)
        {
            lockScreenDebounceTimer!.Stop();
            var token = lockScreenApplyCts?.Token ?? CancellationToken.None;
            _ = ApplyLockScreenPreviewAsync(token);
        }

        private async Task ApplyLockScreenPreviewAsync(CancellationToken token)
        {
            if (mainWindow == null) return;

            var config = mainWindow.Settings.LockScreenAmbient;

            try
            {
                bool success;

                if (config.UsePreset)
                {
                    if (config.PresetId <= 0) return;

                    success = await mainWindow.EngineHost.PreviewWledPresetAsync(config.PresetId, token);
                }
                else
                {
                    if (LockScreenEffectComboBox.SelectedIndex < 0) return;

                    success = await mainWindow.EngineHost.PreviewWledEffectAsync(
                        config.EffectId, config.Speed, config.Intensity, config.PaletteId,
                        (config.PrimaryColorR, config.PrimaryColorG, config.PrimaryColorB),
                        (config.SecondaryColorR, config.SecondaryColorG, config.SecondaryColorB),
                        config.Brightness, cancellationToken: token);
                }

                if (success) hasSentAnyPreview = true;
            }
            catch (OperationCanceledException)
            {
                // Nowsza zmiana w panelu zastąpiła ten podgląd - oczekiwane.
            }
        }

        // ── Podgląd na żywo dla Bezczynności ─────────────────────────────────────

        private void RequestIdlePreviewDebounced()
        {
            if (mainWindow == null) return;

            idleDebounceTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
            idleDebounceTimer.Stop();

            idleApplyCts?.Cancel();
            idleApplyCts?.Dispose();
            idleApplyCts = new CancellationTokenSource();

            idleDebounceTimer.Interval = EffectDebounceDelay;
            idleDebounceTimer.IsRepeating = false;
            idleDebounceTimer.Tick -= OnIdleDebounceTick;
            idleDebounceTimer.Tick += OnIdleDebounceTick;
            idleDebounceTimer.Start();
        }

        private void OnIdleDebounceTick(object? sender, object e)
        {
            idleDebounceTimer!.Stop();
            var token = idleApplyCts?.Token ?? CancellationToken.None;
            _ = ApplyIdlePreviewAsync(token);
        }

        private async Task ApplyIdlePreviewAsync(CancellationToken token)
        {
            if (mainWindow == null) return;

            var config = mainWindow.Settings.IdleAmbient;

            try
            {
                bool success;

                if (config.UsePreset)
                {
                    if (config.PresetId <= 0) return;

                    success = await mainWindow.EngineHost.PreviewWledPresetAsync(config.PresetId, token);
                }
                else
                {
                    if (IdleEffectComboBox.SelectedIndex < 0) return;

                    success = await mainWindow.EngineHost.PreviewWledEffectAsync(
                        config.EffectId, config.Speed, config.Intensity, config.PaletteId,
                        (config.PrimaryColorR, config.PrimaryColorG, config.PrimaryColorB),
                        (config.SecondaryColorR, config.SecondaryColorG, config.SecondaryColorB),
                        config.Brightness, cancellationToken: token);
                }

                if (success) hasSentAnyPreview = true;
            }
            catch (OperationCanceledException)
            {
                // Nowsza zmiana w panelu zastąpiła ten podgląd - oczekiwane.
            }
        }

        // ── Karta "Autostart i zasobnik" ──

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
            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            mainWindow.Settings.AutoStartAmbilight = true;
            AutoStartDisplayModeComboBox.IsEnabled = true;

            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void AutoStartAmbilightCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            mainWindow.Settings.AutoStartAmbilight = false;
            AutoStartDisplayModeComboBox.IsEnabled = false;

            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void AutoStartDisplayModeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null)
            {
                return;
            }

            if (AutoStartDisplayModeComboBox.SelectedItem is not ComboBoxItem selectedItem ||
                selectedItem.Tag is not string modeTag)
            {
                return;
            }

            mainWindow.Settings.AutoStartDisplayMode = modeTag switch
            {
                "StaticColor" => DisplayMode.StaticColor,
                "WledEffects" => DisplayMode.WledEffects,
                _ => DisplayMode.VideoSync
            };

            mainWindow.SettingsService.Save(mainWindow.Settings);
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

        // ── Lista monitorów ──────────────────────────────────────────────────────

        private void RefreshMonitorsList()
        {
            loadedMonitors = MonitorEnumerationHelper.EnumerateMonitors();

            bool wasLoadingUi = isLoadingUi;
            isLoadingUi = true;

            MonitorComboBox.Items.Clear();
            foreach (MonitorInfoItem monitor in loadedMonitors)
            {
                MonitorComboBox.Items.Add(monitor.DisplayName);
            }

            if (mainWindow != null)
            {
                int savedIndex = loadedMonitors.FindIndex(m =>
                    string.Equals(m.DeviceId, mainWindow.Settings.SelectedMonitorDeviceId, StringComparison.OrdinalIgnoreCase));

                if (savedIndex >= 0)
                {
                    MonitorComboBox.SelectedIndex = savedIndex;
                }
            }

            isLoadingUi = wasLoadingUi;
        }

        private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            if (MonitorComboBox.SelectedIndex < 0 || MonitorComboBox.SelectedIndex >= loadedMonitors.Count) return;

            mainWindow.Settings.SelectedMonitorDeviceId = loadedMonitors[MonitorComboBox.SelectedIndex].DeviceId;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }

        private void RefreshMonitorsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshMonitorsList();
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

        private void LockScreenModeEffectRadio_Checked(object sender, RoutedEventArgs e)
        {
            LockScreenEffectPanel.Visibility = Visibility.Visible;
            LockScreenPresetPanel.Visibility = Visibility.Collapsed;

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.UsePreset = false;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenModePresetRadio_Checked(object sender, RoutedEventArgs e)
        {
            LockScreenEffectPanel.Visibility = Visibility.Collapsed;
            LockScreenPresetPanel.Visibility = Visibility.Visible;

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.UsePreset = true;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        // NOWOŚĆ: wybór presetu z listy zamiast ręcznego numeru.
        private void LockScreenPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            if (LockScreenPresetComboBox.SelectedItem is not WledPresetInfo selectedPreset) return;

            mainWindow.Settings.LockScreenAmbient.PresetId = selectedPreset.PresetId;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenEffectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || LockScreenEffectComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.LockScreenAmbient.EffectId = LockScreenEffectComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenPaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || LockScreenPaletteComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.LockScreenAmbient.PaletteId = LockScreenPaletteComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            LockScreenSpeedValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.Speed = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenIntensitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            LockScreenIntensityValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.Intensity = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            LockScreenBrightnessValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.Brightness = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenPrimaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.PrimaryColorR = args.NewColor.R;
            mainWindow.Settings.LockScreenAmbient.PrimaryColorG = args.NewColor.G;
            mainWindow.Settings.LockScreenAmbient.PrimaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
        }

        private void LockScreenSecondaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.LockScreenAmbient.SecondaryColorR = args.NewColor.R;
            mainWindow.Settings.LockScreenAmbient.SecondaryColorG = args.NewColor.G;
            mainWindow.Settings.LockScreenAmbient.SecondaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestLockScreenPreviewDebounced();
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

        private void IdleModeEffectRadio_Checked(object sender, RoutedEventArgs e)
        {
            IdleEffectPanel.Visibility = Visibility.Visible;
            IdlePresetPanel.Visibility = Visibility.Collapsed;

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.UsePreset = false;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdleModePresetRadio_Checked(object sender, RoutedEventArgs e)
        {
            IdleEffectPanel.Visibility = Visibility.Collapsed;
            IdlePresetPanel.Visibility = Visibility.Visible;

            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.UsePreset = true;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        // NOWOŚĆ: wybór presetu z listy zamiast ręcznego numeru.
        private void IdlePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null) return;
            if (IdlePresetComboBox.SelectedItem is not WledPresetInfo selectedPreset) return;

            mainWindow.Settings.IdleAmbient.PresetId = selectedPreset.PresetId;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdleEffectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || IdleEffectComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.IdleAmbient.EffectId = IdleEffectComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdlePaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingUi || mainWindow == null || IdlePaletteComboBox.SelectedIndex < 0) return;
            mainWindow.Settings.IdleAmbient.PaletteId = IdlePaletteComboBox.SelectedIndex;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdleSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleSpeedValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.Speed = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdleIntensitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleIntensityValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.Intensity = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdleBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            IdleBrightnessValueText.Text = ((int)e.NewValue).ToString();
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.Brightness = (int)e.NewValue;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdlePrimaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.PrimaryColorR = args.NewColor.R;
            mainWindow.Settings.IdleAmbient.PrimaryColorG = args.NewColor.G;
            mainWindow.Settings.IdleAmbient.PrimaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }

        private void IdleSecondaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (isLoadingUi || mainWindow == null) return;
            mainWindow.Settings.IdleAmbient.SecondaryColorR = args.NewColor.R;
            mainWindow.Settings.IdleAmbient.SecondaryColorG = args.NewColor.G;
            mainWindow.Settings.IdleAmbient.SecondaryColorB = args.NewColor.B;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RequestIdlePreviewDebounced();
        }
    }
}