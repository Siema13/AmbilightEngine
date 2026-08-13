using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Hardware;
using AmbilightEngine.Core.SystemState;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;

namespace AmbilightEngine.Pages;

public sealed partial class DashboardPage : Page
{
    private List<string> loadedWledEffects = new();
    private List<string> loadedWledPalettes = new();
    private List<WledEffectMetadata> loadedEffectMetadata = new();

    // Mapowanie: pozycja w (posortowanym) ComboBoksie -> rzeczywiste ID efektu/palety w WLED.
    // Niezbędne, bo SelectedIndex po sortowaniu alfabetycznym nie jest już równy fxId.
    private List<int> effectIndexMap = new();
    private List<int> paletteIndexMap = new();

    private MainWindow? mainWindow;
    private DispatcherQueueTimer? fpsTimer;
    private readonly DispatcherQueue uiDispatcherQueue;
    private bool isLoadingUi = false;

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

        // NOWOŚĆ: przywrócenie zapamiętanego trybu wyświetlania po powrocie na tę stronę.
        isLoadingUi = true;
        switch (mainWindow.Settings.ActiveDisplayMode)
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
                isLoadingUi = false;
                await LoadWledEffectsAsync();
                isLoadingUi = true;
                break;

            default:
                VideoSyncModeRadio.IsChecked = true;
                StaticColorPanel.Visibility = Visibility.Collapsed;
                WledEffectsPanel.Visibility = Visibility.Collapsed;
                break;
        }
        isLoadingUi = false;
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

    private void FpsTimerTick(object? sender, object e)
    {
        UpdateFps();
    }

    private void UpdateFps()
    {
        if (mainWindow is null)
        {
            FpsText.Text = "FPS --";
            return;
        }

        if (!mainWindow.EngineHost.IsCapturing)
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
        if (mainWindow == null)
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
        if (mainWindow == null)
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

        mainWindow.Settings.ActiveDisplayMode = selectedMode;
        mainWindow.SettingsService.Save(mainWindow.Settings);

        StaticColorPanel.Visibility = selectedMode == DisplayMode.StaticColor
            ? Visibility.Visible
            : Visibility.Collapsed;

        WledEffectsPanel.Visibility = selectedMode == DisplayMode.WledEffects
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (selectedMode == DisplayMode.WledEffects)
        {
            // NOWOŚĆ: zwalniamy trwałą sesję "live" PRZED zastosowaniem efektu, aby WLED nie
            // ignorował komendy efektu, myśląc że wciąż jest aktywny ciągły strumień DDP z Video Sync.
            mainWindow.EngineHost.NotifyDisplayModeChanged();
            await LoadWledEffectsAsync();
        }
        else
        {
            // Oddajemy priorytet danym DDP (VideoSync/StaticColor) - inaczej WLED wciąż
            // ignoruje przychodzące ramki z powodu lor:1 ustawionego przy ostatniej komendzie efektu.
            await mainWindow.EngineHost.DisableWledRealtimeOverrideAsync();

            // NOWOŚĆ: włączamy trwałą sesję "live" WLED, żeby Video Sync / Static Color nie
            // zależały od skonfigurowanego w WLED Realtime Timeout - eliminuje to powroty do
            // natywnego efektu WLED przy dłuższych przerwach w dostarczaniu klatek z ekranu
            // (np. gdy obraz jest całkowicie statyczny i Windows Graphics Capture nie generuje
            // nowych ramek nawet przez kilka sekund).
            mainWindow.EngineHost.NotifyDisplayModeChanged();
        }
    }

    private void StaticColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (mainWindow == null)
        {
            return;
        }

        mainWindow.Settings.StaticColorR = args.NewColor.R;
        mainWindow.Settings.StaticColorG = args.NewColor.G;
        mainWindow.Settings.StaticColorB = args.NewColor.B;
        mainWindow.SettingsService.Save(mainWindow.Settings);
    }

    private async void RefreshEffectsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadWledEffectsAsync();
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
        if (mainWindow == null) return;

        SetUiBusy(true);
        StatusInfoBar.IsOpen = true;
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "WLED Effects";
        StatusInfoBar.Message = "Wczytywanie listy efektów z urządzenia...";

        isLoadingUi = true;
        bool effectRestored = false;
        try
        {
            WledEffectComboBox.PlaceholderText = "Wczytywanie efektów...";
            WledEffectComboBox.Items.Clear();
            WledPaletteComboBox.Items.Clear();

            try
            {
                loadedWledEffects = await mainWindow.EngineHost.GetAvailableWledEffectsAsync();
                loadedWledPalettes = await mainWindow.EngineHost.GetAvailableWledPalettesAsync();
                loadedEffectMetadata = await mainWindow.EngineHost.GetWledEffectMetadataAsync();

                if (loadedWledEffects.Count == 0)
                {
                    WledEffectComboBox.PlaceholderText = "Nie udało się wczytać efektów";
                    StatusInfoBar.Severity = InfoBarSeverity.Error;
                    StatusInfoBar.Message = "Nie udało się połączyć z urządzeniem WLED. Sprawdź adres IP i połączenie sieciowe.";
                    return;
                }

                PopulateEffectComboBox();
                PopulatePaletteComboBox();

                effectRestored = RestoreLastEffectState();

                WledEffectComboBox.PlaceholderText = "Wybierz efekt";
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Message = $"Wczytano {loadedWledEffects.Count} efektów i {loadedWledPalettes.Count} palet z urządzenia WLED.";
            }
            catch (Exception ex)
            {
                WledEffectComboBox.PlaceholderText = "Błąd wczytywania efektów";
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Message = $"Błąd podczas komunikacji z WLED: {ex.Message}";
            }
        }
        finally
        {
            isLoadingUi = false;
            SetUiBusy(false);
        }

        // NOWOŚĆ: dopiero TERAZ, po zwolnieniu isLoadingUi i poza blokiem try/finally
        // wczytywania listy, wymuszamy wysłanie odtworzonego stanu do urządzenia WLED.
        // Dzięki temu aplikacja zawsze staje się "źródłem prawdy" dla WLED przy każdym
        // wejściu w ten panel (start aplikacji, przełączenie trybu, kliknięcie Odśwież),
        // niezależnie od tego, co ktoś ustawił na urządzeniu w międzyczasie.
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
        if (mainWindow == null) return false;

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
            255, settings.LastWledPrimaryColorR, settings.LastWledPrimaryColorG, settings.LastWledPrimaryColorB);

        EffectSecondaryColorPicker.Color = Windows.UI.Color.FromArgb(
            255, settings.LastWledSecondaryColorR, settings.LastWledSecondaryColorG, settings.LastWledSecondaryColorB);

        // ApplyEffectMetadataToUi ustawia Custom1-3 / Check1-3 na podstawie SelectedIndex
        // efektu - wywołujemy jawnie tutaj, bo SelectionChanged nic nie zrobi (isLoadingUi).
        ApplyEffectMetadataToUi();

        return effectPosition >= 0;
    }
    // Wypełnia ComboBox efektów alfabetycznie, oznacza efekty wymagające matrycy 2D,
    // i ukrywa zarezerwowane wpisy "RSVD"/"-" (WLED wypełniacze numeracji, które przy
    // wybraniu bezgłośnie przełączają się na Solid - dokumentacja WLED wprost radzi je
    // usuwać z UI). effectIndexMap zachowuje mapowanie pozycja-widoku -> rzeczywiste fxId.
    private void PopulateEffectComboBox()
    {
        var entries = new List<(string DisplayName, string SortKey, int OriginalIndex)>();

        for (int i = 0; i < loadedWledEffects.Count; i++)
        {
            string rawName = loadedWledEffects[i];

            if (string.Equals(rawName, "RSVD", StringComparison.OrdinalIgnoreCase) ||
                rawName.Trim() == "-")
            {
                continue;
            }

            bool requiresMatrix = i < loadedEffectMetadata.Count && loadedEffectMetadata[i].RequiresMatrix2D;
            string displayName = requiresMatrix ? $"{rawName} ⬛ (matryca 2D)" : rawName;

            entries.Add((displayName, rawName, i));
        }

        entries.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

        effectIndexMap = entries.Select(entry => entry.OriginalIndex).ToList();

        foreach (var entry in entries)
        {
            WledEffectComboBox.Items.Add(entry.DisplayName);
        }
    }

    private void PopulatePaletteComboBox()
    {
        var entries = new List<(string Name, int OriginalIndex)>();

        for (int i = 0; i < loadedWledPalettes.Count; i++)
        {
            entries.Add((loadedWledPalettes[i], i));
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        paletteIndexMap = entries.Select(entry => entry.OriginalIndex).ToList();

        foreach (var entry in entries)
        {
            WledPaletteComboBox.Items.Add(entry.Name);
        }
    }

    private async Task ApplyCurrentEffectAsync(CancellationToken cancellationToken)
    {
        int selectedEffectPosition = WledEffectComboBox.SelectedIndex;
        if (selectedEffectPosition < 0 || selectedEffectPosition >= effectIndexMap.Count || mainWindow == null) return;

        int fxId = effectIndexMap[selectedEffectPosition];

        int speed = (int)EffectSpeedSlider.Value;
        int intensity = (int)EffectIntensitySlider.Value;

        int selectedPalettePosition = WledPaletteComboBox.SelectedIndex;
        int paletteId = (selectedPalettePosition >= 0 && selectedPalettePosition < paletteIndexMap.Count)
            ? paletteIndexMap[selectedPalettePosition]
            : 0;

        int brightness = (int)EffectBrightnessSlider.Value;
        int custom1 = (int)Custom1Slider.Value;
        int custom2 = (int)Custom2Slider.Value;
        int custom3 = (int)Custom3Slider.Value;
        bool check1 = Check1CheckBox.IsChecked ?? false;
        bool check2 = Check2CheckBox.IsChecked ?? false;
        bool check3 = Check3CheckBox.IsChecked ?? false;

        var primaryColor = (EffectPrimaryColorPicker.Color.R, EffectPrimaryColorPicker.Color.G, EffectPrimaryColorPicker.Color.B);
        var secondaryColor = (EffectSecondaryColorPicker.Color.R, EffectSecondaryColorPicker.Color.G, EffectSecondaryColorPicker.Color.B);

        try
        {
            var settings = mainWindow.Settings;

            // Zachowane dla kompatybilności z istniejącym kodem korzystającym z SelectedWledEffectId.
            settings.SelectedWledEffectId = fxId.ToString();

            // Pełny stan ostatnio zastosowanego efektu - wykorzystywany przez RestoreLastEffectState().
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
                fxId, speed, intensity, paletteId, primaryColor, secondaryColor, brightness,
                custom1, custom2, custom3, check1, check2, check3, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!success)
            {
                StatusInfoBar.IsOpen = true;
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "WLED Effects";
                StatusInfoBar.Message = "Nie udało się zastosować efektu. Urządzenie może być offline.";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }


    private void RequestApplyEffectDebounced()
    {
        if (mainWindow == null) return;

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
        effectDebounceTimer!.Stop();
        var token = effectApplyCts?.Token ?? CancellationToken.None;
        _ = ApplyCurrentEffectAsync(token);
    }

    private void WledEffectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (mainWindow == null) return;

        ApplyEffectMetadataToUi();

        if (isLoadingUi) return;
        RequestApplyEffectDebounced();
    }

    // Wymuszamy, żeby przy otwarciu listy widok przewinął się do WYBRANEGO elementu na
    // górze, a nie centrował go na środku widocznego obszaru (domyślne zachowanie WinUI
    // dla dużych list, które przy 220 efektach wygląda jak "otwarcie od połowy listy").
    private void EffectComboBox_DropDownOpened(object sender, object e)
    {
        if (WledEffectComboBox.SelectedIndex < 0) return;

        uiDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var container = WledEffectComboBox.ContainerFromIndex(WledEffectComboBox.SelectedIndex) as FrameworkElement;
            container?.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.0 });
        });
    }

    private void PaletteComboBox_DropDownOpened(object sender, object e)
    {
        if (WledPaletteComboBox.SelectedIndex < 0) return;

        uiDispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var container = WledPaletteComboBox.ContainerFromIndex(WledPaletteComboBox.SelectedIndex) as FrameworkElement;
            container?.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.0 });
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

        var meta = loadedEffectMetadata[index];

        Matrix2DWarningBar.IsOpen = meta.RequiresMatrix2D;

        bool wasLoadingUi = isLoadingUi;
        isLoadingUi = true;

        Custom1Panel.Visibility = meta.HasCustom1 ? Visibility.Visible : Visibility.Collapsed;
        Custom1Label.Text = meta.Custom1Label;
        Custom2Panel.Visibility = meta.HasCustom2 ? Visibility.Visible : Visibility.Collapsed;
        Custom2Label.Text = meta.Custom2Label;
        Custom3Panel.Visibility = meta.HasCustom3 ? Visibility.Visible : Visibility.Collapsed;
        Custom3Label.Text = meta.Custom3Label;

        Check1CheckBox.Visibility = meta.HasCheck1 ? Visibility.Visible : Visibility.Collapsed;
        Check1CheckBox.Content = meta.Check1Label;
        Check2CheckBox.Visibility = meta.HasCheck2 ? Visibility.Visible : Visibility.Collapsed;
        Check2CheckBox.Content = meta.Check2Label;
        Check3CheckBox.Visibility = meta.HasCheck3 ? Visibility.Visible : Visibility.Collapsed;
        Check3CheckBox.Content = meta.Check3Label;

        WledPaletteComboBox.Visibility = meta.HasPalette ? Visibility.Visible : Visibility.Collapsed;

        if (mainWindow != null)
        {
            int c1 = ParseDefaultOrFallback(meta, "c1", mainWindow.Settings.LastWledCustom1);
            int c2 = ParseDefaultOrFallback(meta, "c2", mainWindow.Settings.LastWledCustom2);
            int c3 = ParseDefaultOrFallback(meta, "c3", mainWindow.Settings.LastWledCustom3);

            Custom1Slider.Value = c1;
            Custom1ValueText.Text = c1.ToString();
            Custom2Slider.Value = c2;
            Custom2ValueText.Text = c2.ToString();
            Custom3Slider.Value = c3;
            Custom3ValueText.Text = c3.ToString();

            Check1CheckBox.IsChecked = ParseDefaultBoolOrFallback(meta, "o1", mainWindow.Settings.LastWledCheck1);
            Check2CheckBox.IsChecked = ParseDefaultBoolOrFallback(meta, "o2", mainWindow.Settings.LastWledCheck2);
            Check3CheckBox.IsChecked = ParseDefaultBoolOrFallback(meta, "o3", mainWindow.Settings.LastWledCheck3);
        }

        isLoadingUi = wasLoadingUi;
    }

    private static int ParseDefaultOrFallback(WledEffectMetadata meta, string key, int fallback)
    {
        return meta.Defaults.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed)
            ? parsed
            : fallback;
    }

    private static bool ParseDefaultBoolOrFallback(WledEffectMetadata meta, string key, bool fallback)
    {
        return meta.Defaults.TryGetValue(key, out string? value) && value == "1"
            ? true
            : fallback;
    }

    private void Custom1Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        Custom1ValueText.Text = ((int)e.NewValue).ToString();
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void Custom2Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        Custom2ValueText.Text = ((int)e.NewValue).ToString();
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void Custom3Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        Custom3ValueText.Text = ((int)e.NewValue).ToString();
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void CheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void WledPaletteComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void EffectPrimaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void EffectSecondaryColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (isLoadingUi || mainWindow == null) return;
        RequestApplyEffectDebounced();
    }

    private void EffectSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isLoadingUi || mainWindow == null) return;
        EffectSpeedValueText.Text = $"{(int)e.NewValue}";
        RequestApplyEffectDebounced();
    }

    private void EffectIntensitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isLoadingUi || mainWindow == null) return;
        EffectIntensityValueText.Text = $"{(int)e.NewValue}";
        RequestApplyEffectDebounced();
    }

    private void EffectBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isLoadingUi || mainWindow == null) return;
        EffectBrightnessValueText.Text = $"{(int)e.NewValue}";
        RequestApplyEffectDebounced();
    }

    private async void ToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (mainWindow is null)
        {
            ApplyStatus(EngineStatusInfo.Error("Brak dostępu do głównego okna aplikacji."));
            return;
        }

        try
        {
            if (mainWindow.EngineHost.IsCapturing)
            {
                mainWindow.EngineHost.StopCapture();
                ApplyStatus(mainWindow.EngineHost.CurrentStatus);
                UpdateFps();
            }
            else
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(mainWindow);
                await mainWindow.EngineHost.StartCaptureAsync(hwnd);
                ApplyStatus(mainWindow.EngineHost.CurrentStatus);
                UpdateFps();
            }
        }
        catch (Exception ex)
        {
            ApplyStatus(EngineStatusInfo.Error($"Błąd przełączania silnika: {ex.Message}"));
            UpdateFps();
        }
    }
}