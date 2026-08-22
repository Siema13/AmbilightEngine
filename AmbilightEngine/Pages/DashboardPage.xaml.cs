using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Models;
using AmbilightEngine.Core.SystemState;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.Models;

namespace AmbilightEngine.Pages;

public sealed partial class DashboardPage : Page
{
    private List<string> loadedWledEffects = new();
    private List<string> loadedWledPalettes = new();
    private List<WledEffectMetadata> loadedEffectMetadata = new();
    private readonly WledPresetService presetService = new();
    private List<WledPresetInfo> loadedPresets = new();
    private List<int> effectIndexMap = new();
    private List<int> paletteIndexMap = new();

    private MainWindow? mainWindow;
    private DispatcherQueueTimer? fpsTimer;
    private readonly DispatcherQueue uiDispatcherQueue;

    private bool isLoadingUi;
    private bool isApplyingDisplayMode;
    private bool isApplyingMasterBrightness;

    private DispatcherQueueTimer? effectDebounceTimer;
    private CancellationTokenSource? effectApplyCts;

    private static readonly TimeSpan EffectDebounceDelay = TimeSpan.FromMilliseconds(120);

    public DashboardPage()
    {
        InitializeComponent();

        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Loaded += DashboardPageLoaded;
        Unloaded += DashboardPageUnloaded;
    }

    private async void DashboardPageLoaded(object sender, RoutedEventArgs e)
    {
        mainWindow = Application.Current is App { MainAppWindow: not null } app
            ? app.MainAppWindow
            : null;

        if (mainWindow is null)
        {
            ApplyStatus(EngineStatusInfo.Error("Nie znaleziono głównego okna aplikacji."));
            FpsText.Text = "FPS --";
            return;
        }

        mainWindow.EngineHost.StatusChanged -= OnStatusChanged;
        mainWindow.EngineHost.StatusChanged += OnStatusChanged;

        ApplyStatus(mainWindow.EngineHost.CurrentStatus);

        WledPreviewControl.Configure(mainWindow.Settings);

        fpsTimer ??= uiDispatcherQueue.CreateTimer();
        fpsTimer.Interval = TimeSpan.FromMilliseconds(500);
        fpsTimer.Tick -= FpsTimerTick;
        fpsTimer.Tick += FpsTimerTick;
        fpsTimer.Start();

        UpdateFps();

        isLoadingUi = true;

        try
        {
            MasterBrightnessSlider.Value =
    mainWindow.Settings.MasterBrightnessPercent;

            MasterBrightnessValueText.Text =
                $"{mainWindow.Settings.MasterBrightnessPercent}%";
            ApplyDisplayModeToUi(mainWindow.Settings.ActiveDisplayMode);
            RefreshScenesList();
            if (mainWindow.Settings.ActiveDisplayMode == DisplayMode.WledEffects)
            {
                await LoadWledEffectsAsync();
            }
            // NOWOŚĆ: presety wczytują się automatycznie przy otwarciu Dashboardu,
            // niezależnie od aktywnego trybu wyświetlania - użytkownik nie musi już
            // klikać "Wczytaj presety" przy każdej wizycie na stronie. Przycisk
            // LoadPresetsButton zostaje jako opcja ręcznego odświeżenia (np. po
            // dodaniu nowego presetu w aplikacji webowej WLED).
            await LoadPresetsAutomaticallyAsync();
        }
        finally
        {
            isLoadingUi = false;
        }
    }

    private void DashboardPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (mainWindow is not null)
        {
            mainWindow.EngineHost.StatusChanged -= OnStatusChanged;
        }

        if (fpsTimer is not null)
        {
            fpsTimer.Tick -= FpsTimerTick;
            fpsTimer.Stop();
        }

        effectDebounceTimer?.Stop();

        effectApplyCts?.Cancel();
        effectApplyCts?.Dispose();
        effectApplyCts = null;
    }

    private void ApplyDisplayModeToUi(DisplayMode displayMode)
    {
        switch (displayMode)
        {
            case DisplayMode.StaticColor:
                StaticColorModeRadio.IsChecked = true;
                StaticColorPanel.Visibility = Visibility.Visible;
                WledEffectsPanel.Visibility = Visibility.Collapsed;
                break;

            case DisplayMode.WledEffects:
                WledEffectsModeRadio.IsChecked = true;
                StaticColorPanel.Visibility = Visibility.Collapsed;
                WledEffectsPanel.Visibility = Visibility.Visible;
                break;

            case DisplayMode.VideoSync:
            default:
                VideoSyncModeRadio.IsChecked = true;
                StaticColorPanel.Visibility = Visibility.Collapsed;
                WledEffectsPanel.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void FpsTimerTick(object? sender, object e)
    {
        UpdateFps();
    }

    private void UpdateFps()
    {
        if (mainWindow is null || !mainWindow.EngineHost.IsCapturing)
        {
            FpsText.Text = "FPS --";
            return;
        }

        double captureFps = mainWindow.EngineHost.CaptureFps;
        double sendFps = mainWindow.EngineHost.SendFps;

        FpsText.Text = $"Capture FPS {captureFps:F1} | Send FPS {sendFps:F1}";
    }

    private void OnStatusChanged(EngineStatusInfo status)
    {
        uiDispatcherQueue.TryEnqueue(() =>
        {
            ApplyStatus(status);
            UpdateFps();
        });
    }

    private void ApplyStatus(EngineStatusInfo status)
    {
        StatusText.Text = status.Message;
        StatusInfoBar.Message = status.Message;

        switch (status.State)
        {
            case EngineRunState.Starting:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "Uruchamianie";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = false;
                break;

            case EngineRunState.Running:
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Ambilight aktywny";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                break;

            case EngineRunState.Ambient:
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "Tryb ambientowy";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                break;

            case EngineRunState.Error:
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Błąd silnika";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                break;

            case EngineRunState.Stopped:
            default:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "Silnik zatrzymany";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                break;
        }

        UpdateToggleButtonContent();
    }

    private void UpdateToggleButtonContent()
    {
        if (mainWindow is null)
        {
            ToggleButton.Content = "Wybierz monitor i uruchom Ambilight";
            return;
        }

        if (mainWindow.EngineHost.CurrentStatus.State == EngineRunState.Starting)
        {
            ToggleButton.Content = "Uruchamianie...";
            return;
        }

        ToggleButton.Content = mainWindow.EngineHost.IsCapturing
            ? "Zatrzymaj Ambilight"
            : "Wybierz monitor i uruchom Ambilight";
    }

    private async void DisplayModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (isLoadingUi || isApplyingDisplayMode || mainWindow is null)
        {
            return;
        }

        if (sender is not RadioButton radio || radio.Tag is not string modeTag)
        {
            return;
        }

        DisplayMode selectedMode = modeTag switch
        {
            "StaticColor" => DisplayMode.StaticColor,
            "WledEffects" => DisplayMode.WledEffects,
            _ => DisplayMode.VideoSync
        };

        if (mainWindow.Settings.ActiveDisplayMode == selectedMode)
        {
            return;
        }

        isApplyingDisplayMode = true;

        try
        {
            StaticColorPanel.Visibility = selectedMode == DisplayMode.StaticColor
                ? Visibility.Visible
                : Visibility.Collapsed;

            WledEffectsPanel.Visibility = selectedMode == DisplayMode.WledEffects
                ? Visibility.Visible
                : Visibility.Collapsed;

            switch (selectedMode)
            {
                case DisplayMode.StaticColor:
                    await mainWindow.EngineHost.ApplyStaticColorWithTransitionAsync(
                        mainWindow.Settings.StaticColorR,
                        mainWindow.Settings.StaticColorG,
                        mainWindow.Settings.StaticColorB);

                    break;

                case DisplayMode.WledEffects:
                    mainWindow.Settings.ActiveDisplayMode = DisplayMode.WledEffects;
                    mainWindow.EngineHost.NotifyDisplayModeChanged();

                    await LoadWledEffectsAsync();
                    break;

                case DisplayMode.VideoSync:
                default:
                    await mainWindow.EngineHost.ApplyVideoSyncWithTransitionAsync();
                    break;
            }

            mainWindow.SettingsService.Save(mainWindow.Settings);
        }
        catch (Exception ex)
        {
            ApplyStatus(EngineStatusInfo.Error(
                $"Nie udało się przełączyć trybu wyświetlania: {ex.Message}"));
        }
        finally
        {
            isApplyingDisplayMode = false;
        }
    }

    private async void StaticColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (mainWindow is null || isLoadingUi || isApplyingDisplayMode)
        {
            return;
        }

        mainWindow.Settings.StaticColorR = args.NewColor.R;
        mainWindow.Settings.StaticColorG = args.NewColor.G;
        mainWindow.Settings.StaticColorB = args.NewColor.B;

        mainWindow.SettingsService.Save(mainWindow.Settings);

        if (mainWindow.Settings.ActiveDisplayMode != DisplayMode.StaticColor)
        {
            return;
        }

        await mainWindow.EngineHost.ApplyStaticColorWithTransitionAsync(
            args.NewColor.R,
            args.NewColor.G,
            args.NewColor.B);
    }

    private async void RefreshEffectsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadWledEffectsAsync();
    }
    private async Task LoadPresetsAutomaticallyAsync()
    {
        if (mainWindow is null)
        {
            return;
        }

        try
        {
            loadedPresets = await presetService.GetPresetsAsync(mainWindow.Settings.EspIpAddress);

            WledPresetComboBox.ItemsSource = loadedPresets;

            if (loadedPresets.Count > 0)
            {
                PresetsInfoBar.Severity = InfoBarSeverity.Success;
                PresetsInfoBar.Message = $"Wczytano automatycznie {loadedPresets.Count} presetów/playlist z urządzenia WLED.";
                PresetsInfoBar.IsOpen = true;
            }
            // Brak presetów przy automatycznym ładowaniu nie pokazuje błędu - urządzenie
            // mogło być offline przy starcie aplikacji; użytkownik może kliknąć
            // "Wczytaj presety" ręcznie, kiedy WLED będzie dostępne.
        }
        catch (Exception)
        {
            // Cichy fallback - błąd połączenia przy automatycznym ładowaniu nie
            // powinien przeszkadzać w korzystaniu z reszty Dashboardu.
        }
    }
    private async void LoadPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null)
        {
            return;
        }

        LoadPresetsButton.IsEnabled = false;
        PresetsInfoBar.IsOpen = true;
        PresetsInfoBar.Severity = InfoBarSeverity.Informational;
        PresetsInfoBar.Message = "Wczytywanie presetów z urządzenia...";

        try
        {
            loadedPresets = await presetService.GetPresetsAsync(mainWindow.Settings.EspIpAddress);

            WledPresetComboBox.ItemsSource = loadedPresets;

            if (loadedPresets.Count == 0)
            {
                PresetsInfoBar.Severity = InfoBarSeverity.Warning;
                PresetsInfoBar.Message = "Nie znaleziono żadnych zapisanych presetów na urządzeniu WLED.";
            }
            else
            {
                PresetsInfoBar.Severity = InfoBarSeverity.Success;
                PresetsInfoBar.Message = $"Wczytano {loadedPresets.Count} presetów/playlist z urządzenia WLED.";
            }
        }
        catch (Exception ex)
        {
            PresetsInfoBar.Severity = InfoBarSeverity.Error;
            PresetsInfoBar.Message = $"Błąd podczas komunikacji z WLED: {ex.Message}";
        }
        finally
        {
            LoadPresetsButton.IsEnabled = true;
        }
    }

    private void WledPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WledPresetComboBox.SelectedItem is not WledPresetInfo selected)
        {
            ActivatePresetButton.IsEnabled = false;
            SelectedPresetDetailsText.Text = string.Empty;
            return;
        }

        ActivatePresetButton.IsEnabled = true;

        SelectedPresetDetailsText.Text = selected.IsPlaylist
            ? $"Playlista: {selected.Playlist!.PresetSequence.Count} kroków, powtórzeń: " +
              $"{(selected.Playlist.RepeatCount == 0 ? "w kółko" : selected.Playlist.RepeatCount.ToString())}."
            : $"Preset numer {selected.PresetId}.";
    }

    private async void ActivatePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null || WledPresetComboBox.SelectedItem is not WledPresetInfo selected)
        {
            return;
        }

        ActivatePresetButton.IsEnabled = false;

        try
        {
            bool success = await presetService.ActivatePresetAsync(
                mainWindow.Settings.EspIpAddress,
                selected.PresetId);

            PresetsInfoBar.IsOpen = true;

            if (success)
            {
                PresetsInfoBar.Severity = InfoBarSeverity.Success;
                PresetsInfoBar.Message = $"Aktywowano „{selected.DisplayName}”.";
            }
            else
            {
                PresetsInfoBar.Severity = InfoBarSeverity.Error;
                PresetsInfoBar.Message = "Nie udało się aktywować presetu. Urządzenie może być offline.";
            }
        }
        finally
        {
            ActivatePresetButton.IsEnabled = true;
        }
    }
    private void SetUiBusy(bool isBusy)
    {
        WledEffectComboBox.IsEnabled = !isBusy;
        WledPaletteComboBox.IsEnabled = !isBusy;
        EffectSpeedSlider.IsEnabled = !isBusy;
        EffectIntensitySlider.IsEnabled = !isBusy;
        RefreshEffectsButton.IsEnabled = !isBusy;
    }

    private async Task LoadWledEffectsAsync()
    {
        if (mainWindow is null)
        {
            return;
        }

        SetUiBusy(true);

        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "WLED Effects";
        StatusInfoBar.Message = "Wczytywanie listy efektów z urządzenia...";

        bool previousLoadingState = isLoadingUi;
        isLoadingUi = true;

        bool effectRestored = false;

        try
        {
            WledEffectComboBox.PlaceholderText = "Wczytywanie efektów...";
            WledEffectComboBox.Items.Clear();
            WledPaletteComboBox.Items.Clear();

            loadedWledEffects = await mainWindow.EngineHost.GetAvailableWledEffectsAsync();
            loadedWledPalettes = await mainWindow.EngineHost.GetAvailableWledPalettesAsync();
            loadedEffectMetadata = await mainWindow.EngineHost.GetWledEffectMetadataAsync();

            if (loadedWledEffects.Count == 0)
            {
                WledEffectComboBox.PlaceholderText = "Nie udało się wczytać efektów";
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Message =
                    "Nie udało się połączyć z urządzeniem WLED. Sprawdź adres IP i połączenie sieciowe.";

                return;
            }

            PopulateEffectComboBox();
            PopulatePaletteComboBox();

            effectRestored = RestoreLastEffectState();

            WledEffectComboBox.PlaceholderText = "Wybierz efekt";
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Message =
                $"Wczytano {loadedWledEffects.Count} efektów i {loadedWledPalettes.Count} palet z urządzenia WLED.";
        }
        catch (Exception ex)
        {
            WledEffectComboBox.PlaceholderText = "Błąd wczytywania efektów";
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Message = $"Błąd podczas komunikacji z WLED: {ex.Message}";
        }
        finally
        {
            isLoadingUi = previousLoadingState;
            SetUiBusy(false);
        }

        if (effectRestored)
        {
            effectApplyCts?.Cancel();
            effectApplyCts?.Dispose();

            effectApplyCts = new CancellationTokenSource();

            await ApplyCurrentEffectAsync(effectApplyCts.Token);
        }
    }

    private bool RestoreLastEffectState()
    {
        if (mainWindow is null)
        {
            return false;
        }

        var settings = mainWindow.Settings;

        int effectPosition = effectIndexMap.IndexOf(settings.LastWledEffectId);
        if (effectPosition >= 0)
        {
            WledEffectComboBox.SelectedIndex = effectPosition;
        }

        int palettePosition = paletteIndexMap.IndexOf(settings.LastWledPaletteId);
        if (palettePosition >= 0)
        {
            WledPaletteComboBox.SelectedIndex = palettePosition;
        }

        EffectSpeedSlider.Value = settings.LastWledSpeed;
        EffectSpeedValueText.Text = settings.LastWledSpeed.ToString();

        EffectIntensitySlider.Value = settings.LastWledIntensity;
        EffectIntensityValueText.Text = settings.LastWledIntensity.ToString();

        EffectBrightnessSlider.Value = settings.LastWledBrightness;
        EffectBrightnessValueText.Text = settings.LastWledBrightness.ToString();

        EffectPrimaryColorPicker.Color = Windows.UI.Color.FromArgb(
            255,
            settings.LastWledPrimaryColorR,
            settings.LastWledPrimaryColorG,
            settings.LastWledPrimaryColorB);

        EffectSecondaryColorPicker.Color = Windows.UI.Color.FromArgb(
            255,
            settings.LastWledSecondaryColorR,
            settings.LastWledSecondaryColorG,
            settings.LastWledSecondaryColorB);

        ApplyEffectMetadataToUi();

        return effectPosition >= 0;
    }

    private void PopulateEffectComboBox()
    {
        var entries = new List<(string DisplayName, string SortKey, int OriginalIndex)>();

        for (int index = 0; index < loadedWledEffects.Count; index++)
        {
            string rawName = loadedWledEffects[index];

            if (string.Equals(rawName, "RSVD", StringComparison.OrdinalIgnoreCase) ||
                rawName.Trim() == "-")
            {
                continue;
            }

            bool requiresMatrix =
                index < loadedEffectMetadata.Count &&
                loadedEffectMetadata[index].RequiresMatrix2D;

            string displayName = requiresMatrix
                ? $"{rawName} ⬛ (matryca 2D)"
                : rawName;

            entries.Add((displayName, rawName, index));
        }

        entries.Sort((left, right) =>
            string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));

        effectIndexMap = entries.Select(entry => entry.OriginalIndex).ToList();

        foreach (var entry in entries)
        {
            WledEffectComboBox.Items.Add(entry.DisplayName);
        }
    }

    private void PopulatePaletteComboBox()
    {
        var entries = new List<(string Name, int OriginalIndex)>();

        for (int index = 0; index < loadedWledPalettes.Count; index++)
        {
            entries.Add((loadedWledPalettes[index], index));
        }

        entries.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        paletteIndexMap = entries.Select(entry => entry.OriginalIndex).ToList();

        foreach (var entry in entries)
        {
            WledPaletteComboBox.Items.Add(entry.Name);
        }
    }

    private async Task ApplyCurrentEffectAsync(CancellationToken cancellationToken)
    {
        int selectedEffectPosition = WledEffectComboBox.SelectedIndex;

        if (selectedEffectPosition < 0 ||
            selectedEffectPosition >= effectIndexMap.Count ||
            mainWindow is null)
        {
            return;
        }

        int fxId = effectIndexMap[selectedEffectPosition];

        int speed = (int)EffectSpeedSlider.Value;
        int intensity = (int)EffectIntensitySlider.Value;

        int selectedPalettePosition = WledPaletteComboBox.SelectedIndex;

        int paletteId =
            selectedPalettePosition >= 0 &&
            selectedPalettePosition < paletteIndexMap.Count
                ? paletteIndexMap[selectedPalettePosition]
                : 0;

        int brightness = (int)EffectBrightnessSlider.Value;
        int custom1 = (int)Custom1Slider.Value;
        int custom2 = (int)Custom2Slider.Value;
        int custom3 = (int)Custom3Slider.Value;

        bool check1 = Check1CheckBox.IsChecked ?? false;
        bool check2 = Check2CheckBox.IsChecked ?? false;
        bool check3 = Check3CheckBox.IsChecked ?? false;

        var primaryColor = (
            EffectPrimaryColorPicker.Color.R,
            EffectPrimaryColorPicker.Color.G,
            EffectPrimaryColorPicker.Color.B);

        var secondaryColor = (
            EffectSecondaryColorPicker.Color.R,
            EffectSecondaryColorPicker.Color.G,
            EffectSecondaryColorPicker.Color.B);

        try
        {
            var settings = mainWindow.Settings;

            settings.SelectedWledEffectId = fxId.ToString();
            settings.LastWledEffectId = fxId;
            settings.LastWledPaletteId = paletteId;
            settings.LastWledSpeed = speed;
            settings.LastWledIntensity = intensity;
            settings.LastWledBrightness = brightness;

            settings.LastWledPrimaryColorR = primaryColor.Item1;
            settings.LastWledPrimaryColorG = primaryColor.Item2;
            settings.LastWledPrimaryColorB = primaryColor.Item3;

            settings.LastWledSecondaryColorR = secondaryColor.Item1;
            settings.LastWledSecondaryColorG = secondaryColor.Item2;
            settings.LastWledSecondaryColorB = secondaryColor.Item3;

            settings.LastWledCustom1 = custom1;
            settings.LastWledCustom2 = custom2;
            settings.LastWledCustom3 = custom3;

            settings.LastWledCheck1 = check1;
            settings.LastWledCheck2 = check2;
            settings.LastWledCheck3 = check3;

            mainWindow.SettingsService.Save(settings);

            bool success = await mainWindow.EngineHost.ActivateWledEffectAsync(
                fxId,
                speed,
                intensity,
                paletteId,
                primaryColor,
                secondaryColor,
                brightness,
                custom1,
                custom2,
                custom3,
                check1,
                check2,
                check3,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!success)
            {
                StatusInfoBar.IsOpen = true;
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "WLED Effects";
                StatusInfoBar.Message =
                    "Nie udało się zastosować efektu. Urządzenie może być offline.";
            }
        }
        catch (OperationCanceledException)
        {
            // Poprzednie żądanie efektu zostało zastąpione nowszym.
        }
    }

    private void RequestApplyEffectDebounced()
    {
        if (mainWindow is null)
        {
            return;
        }

        effectDebounceTimer ??= uiDispatcherQueue.CreateTimer();
        effectDebounceTimer.Stop();

        effectApplyCts?.Cancel();
        effectApplyCts?.Dispose();

        effectApplyCts = new CancellationTokenSource();

        effectDebounceTimer.Interval = EffectDebounceDelay;
        effectDebounceTimer.IsRepeating = false;

        effectDebounceTimer.Tick -= OnEffectDebounceTick;
        effectDebounceTimer.Tick += OnEffectDebounceTick;
        effectDebounceTimer.Start();
    }

    private void OnEffectDebounceTick(object? sender, object e)
    {
        effectDebounceTimer?.Stop();

        CancellationToken token = effectApplyCts?.Token ?? CancellationToken.None;

        _ = ApplyCurrentEffectAsync(token);
    }

    private void WledEffectComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (mainWindow is null)
        {
            return;
        }

        ApplyEffectMetadataToUi();

        if (isLoadingUi)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void EffectComboBox_DropDownOpened(object sender, object e)
    {
        if (WledEffectComboBox.SelectedIndex < 0)
        {
            return;
        }

        uiDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var container = WledEffectComboBox.ContainerFromIndex(
                WledEffectComboBox.SelectedIndex) as FrameworkElement;

            container?.StartBringIntoView(
                new BringIntoViewOptions { VerticalAlignmentRatio = 0.0 });
        });
    }

    private void PaletteComboBox_DropDownOpened(object sender, object e)
    {
        if (WledPaletteComboBox.SelectedIndex < 0)
        {
            return;
        }

        uiDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var container = WledPaletteComboBox.ContainerFromIndex(
                WledPaletteComboBox.SelectedIndex) as FrameworkElement;

            container?.StartBringIntoView(
                new BringIntoViewOptions { VerticalAlignmentRatio = 0.0 });
        });
    }

    private void ApplyEffectMetadataToUi()
    {
        int selectedPosition = WledEffectComboBox.SelectedIndex;

        if (selectedPosition < 0 || selectedPosition >= effectIndexMap.Count)
        {
            Matrix2DWarningBar.IsOpen = false;
            return;
        }

        int index = effectIndexMap[selectedPosition];

        if (index >= loadedEffectMetadata.Count)
        {
            Matrix2DWarningBar.IsOpen = false;
            return;
        }

        var metadata = loadedEffectMetadata[index];

        Matrix2DWarningBar.IsOpen = metadata.RequiresMatrix2D;

        bool previousLoadingState = isLoadingUi;
        isLoadingUi = true;

        try
        {
            Custom1Panel.Visibility =
                metadata.HasCustom1 ? Visibility.Visible : Visibility.Collapsed;

            Custom1Label.Text = metadata.Custom1Label;

            Custom2Panel.Visibility =
                metadata.HasCustom2 ? Visibility.Visible : Visibility.Collapsed;

            Custom2Label.Text = metadata.Custom2Label;

            Custom3Panel.Visibility =
                metadata.HasCustom3 ? Visibility.Visible : Visibility.Collapsed;

            Custom3Label.Text = metadata.Custom3Label;

            Check1CheckBox.Visibility =
                metadata.HasCheck1 ? Visibility.Visible : Visibility.Collapsed;

            Check1CheckBox.Content = metadata.Check1Label;

            Check2CheckBox.Visibility =
                metadata.HasCheck2 ? Visibility.Visible : Visibility.Collapsed;

            Check2CheckBox.Content = metadata.Check2Label;

            Check3CheckBox.Visibility =
                metadata.HasCheck3 ? Visibility.Visible : Visibility.Collapsed;

            Check3CheckBox.Content = metadata.Check3Label;

            WledPaletteComboBox.Visibility =
                metadata.HasPalette ? Visibility.Visible : Visibility.Collapsed;

            if (mainWindow is null)
            {
                return;
            }

            int custom1 = ParseDefaultOrFallback(
                metadata,
                "c1",
                mainWindow.Settings.LastWledCustom1);

            int custom2 = ParseDefaultOrFallback(
                metadata,
                "c2",
                mainWindow.Settings.LastWledCustom2);

            int custom3 = ParseDefaultOrFallback(
                metadata,
                "c3",
                mainWindow.Settings.LastWledCustom3);

            Custom1Slider.Value = custom1;
            Custom1ValueText.Text = custom1.ToString();

            Custom2Slider.Value = custom2;
            Custom2ValueText.Text = custom2.ToString();

            Custom3Slider.Value = custom3;
            Custom3ValueText.Text = custom3.ToString();

            Check1CheckBox.IsChecked = ParseDefaultBoolOrFallback(
                metadata,
                "o1",
                mainWindow.Settings.LastWledCheck1);

            Check2CheckBox.IsChecked = ParseDefaultBoolOrFallback(
                metadata,
                "o2",
                mainWindow.Settings.LastWledCheck2);

            Check3CheckBox.IsChecked = ParseDefaultBoolOrFallback(
                metadata,
                "o3",
                mainWindow.Settings.LastWledCheck3);
        }
        finally
        {
            isLoadingUi = previousLoadingState;
        }
    }

    private static int ParseDefaultOrFallback(
        WledEffectMetadata metadata,
        string key,
        int fallback)
    {
        return metadata.Defaults.TryGetValue(key, out string? value) &&
               int.TryParse(value, out int parsed)
            ? parsed
            : fallback;
    }

    private static bool ParseDefaultBoolOrFallback(
        WledEffectMetadata metadata,
        string key,
        bool fallback)
    {
        return metadata.Defaults.TryGetValue(key, out string? value) && value == "1"
            ? true
            : fallback;
    }

    private void Custom1Slider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        Custom1ValueText.Text = ((int)e.NewValue).ToString();

        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void Custom2Slider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        Custom2ValueText.Text = ((int)e.NewValue).ToString();

        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void Custom3Slider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        Custom3ValueText.Text = ((int)e.NewValue).ToString();

        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void CheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void WledPaletteComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void EffectPrimaryColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void EffectSecondaryColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void EffectSpeedSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        EffectSpeedValueText.Text = ((int)e.NewValue).ToString();

        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void EffectIntensitySlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        EffectIntensityValueText.Text = ((int)e.NewValue).ToString();

        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private void EffectBrightnessSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        EffectBrightnessValueText.Text = ((int)e.NewValue).ToString();

        if (isLoadingUi || mainWindow is null)
        {
            return;
        }

        RequestApplyEffectDebounced();
    }

    private async void ToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null)
        {
            ApplyStatus(EngineStatusInfo.Error(
                "Brak dostępu do głównego okna aplikacji."));

            return;
        }

        try
        {
            if (mainWindow.EngineHost.IsCapturing)
            {
                mainWindow.EngineHost.StopCapture();
            }
            else
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(mainWindow);

                await mainWindow.EngineHost.StartCaptureAsync(hwnd);
            }

            ApplyStatus(mainWindow.EngineHost.CurrentStatus);
            UpdateFps();
        }
        catch (Exception ex)
        {
            ApplyStatus(EngineStatusInfo.Error(
                $"Błąd przełączania silnika: {ex.Message}"));

            UpdateFps();
        }
    }
    private async void MasterBrightnessSlider_ValueChanged(
    object sender,
    RangeBaseValueChangedEventArgs e)
    {
        int brightnessPercent = (int)Math.Round(e.NewValue);

        MasterBrightnessValueText.Text = $"{brightnessPercent}%";

        if (mainWindow is null ||
            isLoadingUi ||
            isApplyingMasterBrightness)
        {
            return;
        }

        isApplyingMasterBrightness = true;

        try
        {
            bool applied = await mainWindow.EngineHost
                .SetMasterBrightnessPercentAsync(brightnessPercent);

            if (applied)
            {
                mainWindow.SettingsService.Save(mainWindow.Settings);
            }
        }
        catch (Exception ex)
        {
            ApplyStatus(EngineStatusInfo.Error(
                $"Nie udało się zmienić jasności głównej: {ex.Message}"));
        }
        finally
        {
            isApplyingMasterBrightness = false;
        }
    }
    private void RefreshScenesList()
    {
        if (mainWindow is null)
        {
            return;
        }

        ScenesItemsControl.ItemsSource = null;
        ScenesItemsControl.ItemsSource = mainWindow.Settings.Scenes;

        NoScenesText.Visibility = mainWindow.Settings.Scenes.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SaveSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null)
        {
            return;
        }

        var nameTextBox = new TextBox
        {
            PlaceholderText = "Np. Gaming, Film wieczorem, Relaks...",
            MaxLength = 60
        };

        var saveDialog = new ContentDialog
        {
            Title = "Zapisz bieżącą scenę",
            Content = nameTextBox,
            PrimaryButtonText = "Zapisz",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        // Blokada zapisu pustej nazwy - przycisk "Zapisz" staje się aktywny dopiero,
        // gdy użytkownik wpisze co najmniej jeden niepusty znak.
        saveDialog.IsPrimaryButtonEnabled = false;

        nameTextBox.TextChanged += (_, _) =>
        {
            saveDialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text);
        };

        ContentDialogResult result = await saveDialog.ShowAsync();

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        string sceneName = nameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(sceneName))
        {
            return;
        }

        try
        {
            mainWindow.EngineHost.SaveCurrentScene(sceneName);
            mainWindow.SettingsService.Save(mainWindow.Settings);

            RefreshScenesList();

            StatusInfoBar.IsOpen = true;
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Quick Palette";
            StatusInfoBar.Message = $"Zapisano scenę „{sceneName}”.";
        }
        catch (Exception ex)
        {
            StatusInfoBar.IsOpen = true;
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Quick Palette";
            StatusInfoBar.Message = $"Nie udało się zapisać sceny: {ex.Message}";
        }
    }

    private async void RunSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null || sender is not Button button || button.Tag is not string sceneId)
        {
            return;
        }

        SceneProfile? scene = mainWindow.Settings.Scenes.Find(s =>
            string.Equals(s.SceneId, sceneId, StringComparison.Ordinal));

        if (scene is null)
        {
            return;
        }

        button.IsEnabled = false;

        try
        {
            bool applied = await mainWindow.EngineHost.ApplySceneAsync(scene);

            if (applied)
            {
                // Odświeżamy stan radiobuttonów/paneli, żeby UI Dashboardu odzwierciedlał
                // tryb przywrócony przez scenę (np. przełączenie z WLED Effects na Static Color).
                isLoadingUi = true;

                try
                {
                    ApplyDisplayModeToUi(mainWindow.Settings.ActiveDisplayMode);

                    MasterBrightnessSlider.Value = mainWindow.Settings.MasterBrightnessPercent;
                    MasterBrightnessValueText.Text = $"{mainWindow.Settings.MasterBrightnessPercent}%";

                    if (mainWindow.Settings.ActiveDisplayMode == DisplayMode.StaticColor)
                    {
                        StaticColorPicker.Color = Windows.UI.Color.FromArgb(
                            255,
                            mainWindow.Settings.StaticColorR,
                            mainWindow.Settings.StaticColorG,
                            mainWindow.Settings.StaticColorB);
                    }
                }
                finally
                {
                    isLoadingUi = false;
                }

                mainWindow.SettingsService.Save(mainWindow.Settings);

                StatusInfoBar.IsOpen = true;
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Quick Palette";
                StatusInfoBar.Message = $"Uruchomiono scenę „{scene.Name}”.";
            }
            else
            {
                StatusInfoBar.IsOpen = true;
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Quick Palette";
                StatusInfoBar.Message = $"Nie udało się uruchomić sceny „{scene.Name}”.";
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.IsOpen = true;
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Quick Palette";
            StatusInfoBar.Message = $"Błąd podczas uruchamiania sceny: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void DeleteSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null || sender is not Button button || button.Tag is not string sceneId)
        {
            return;
        }

        SceneProfile? scene = mainWindow.Settings.Scenes.Find(s =>
            string.Equals(s.SceneId, sceneId, StringComparison.Ordinal));

        if (scene is null)
        {
            return;
        }

        var confirmDialog = new ContentDialog
        {
            Title = "Usunąć scenę?",
            Content = $"Scena „{scene.Name}” zostanie trwale usunięta z Quick Palette.",
            PrimaryButtonText = "Usuń",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        ContentDialogResult result = await confirmDialog.ShowAsync();

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        bool removed = mainWindow.EngineHost.DeleteScene(sceneId);

        if (removed)
        {
            mainWindow.SettingsService.Save(mainWindow.Settings);
            RefreshScenesList();

            StatusInfoBar.IsOpen = true;
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "Quick Palette";
            StatusInfoBar.Message = $"Usunięto scenę „{scene.Name}”.";
        }
    }
}