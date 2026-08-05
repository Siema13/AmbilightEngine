using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private MainWindow? mainWindow;
    private DispatcherQueueTimer? fpsTimer;
    private readonly DispatcherQueue uiDispatcherQueue;
    private bool isLoadingUi = false;

    // ── Debounce dla efektów WLED ────────────────────────────────────────────
    // Bez tego każdy tick slidera wysyłał osobne żądanie HTTP do ESP32, co przy
    // przeciąganiu slidera generowało dziesiątki żądań kolejkujących się na
    // jednowątkowym serwerze WLED - stąd opóźnienia rzędu 30s-1min.
    private DispatcherQueueTimer? effectDebounceTimer;
    private CancellationTokenSource? effectApplyCts;
    private static readonly TimeSpan EffectDebounceDelay = TimeSpan.FromMilliseconds(120);
    // ──────────────────────────────────────────────────────────────────────────

    public DashboardPage()
    {
        InitializeComponent();

        uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Loaded += DashboardPageLoaded;
        Unloaded += DashboardPageUnloaded;
    }

    private void DashboardPageLoaded(object sender, RoutedEventArgs e)
    {
        mainWindow = Application.Current as App is { MainAppWindow: not null } app
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

        // NOWOŚĆ: podgląd na żywo diod LED (WLED Peek) - niezależny od stanu silnika.
        WledPreviewControl.Configure(mainWindow.Settings);

        fpsTimer ??= uiDispatcherQueue.CreateTimer();
        fpsTimer.Interval = TimeSpan.FromMilliseconds(500);
        fpsTimer.Tick -= FpsTimerTick;
        fpsTimer.Tick += FpsTimerTick;
        fpsTimer.Start();

        UpdateFps();
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

        if (!mainWindow.EngineHost.IsRunning)
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
                ToggleButton.Content = "Uruchamianie...";
                break;

            case EngineRunState.Running:
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Ambilight aktywny";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                ToggleButton.Content = "Zatrzymaj Ambilight";
                break;

            case EngineRunState.Ambient:
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "Tryb ambientowy";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                ToggleButton.Content = "Zatrzymaj Ambilight";
                break;

            case EngineRunState.Error:
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Błąd silnika";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                ToggleButton.Content = "Spróbuj uruchomić ponownie";
                break;

            case EngineRunState.Stopped:
            default:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "Silnik zatrzymany";
                StatusInfoBar.IsOpen = true;
                ToggleButton.IsEnabled = true;
                ToggleButton.Content = "Wybierz monitor i uruchom Ambilight";
                break;
        }
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
            await LoadWledEffectsAsync();
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

        // FIX: populacja ComboBoxów musi być osłonięta flagą isLoadingUi. Items.Clear()
        // i kolejne Items.Add() mogą wywołać SelectionChanged (np. przy automatycznym
        // zaznaczeniu pierwszego elementu przez WinUI) - bez tej osłony każde takie
        // zdarzenie odpalało prawdziwe żądanie HTTP do WLED, mimo że to nie była
        // akcja użytkownika, tylko odświeżenie listy.
        isLoadingUi = true;
        try
        {
            WledEffectComboBox.PlaceholderText = "Wczytywanie efektów...";
            WledEffectComboBox.Items.Clear();
            WledPaletteComboBox.Items.Clear();

            try
            {
                loadedWledEffects = await mainWindow.EngineHost.GetAvailableWledEffectsAsync();
                loadedWledPalettes = await mainWindow.EngineHost.GetAvailableWledPalettesAsync();

                if (loadedWledEffects.Count == 0)
                {
                    WledEffectComboBox.PlaceholderText = "Nie udało się wczytać efektów";
                    StatusInfoBar.Severity = InfoBarSeverity.Error;
                    StatusInfoBar.Message = "Nie udało się połączyć z urządzeniem WLED. Sprawdź adres IP i połączenie sieciowe.";
                    return;
                }

                foreach (string effectName in loadedWledEffects)
                {
                    WledEffectComboBox.Items.Add(effectName);
                }

                foreach (string paletteName in loadedWledPalettes)
                {
                    WledPaletteComboBox.Items.Add(paletteName);
                }

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
    }

    /// <summary>
    /// Wysyła aktualny stan efektu do WLED. Wywoływana z tokenem anulowania,
    /// żeby debounce mógł odrzucić nieaktualne żądania w toku.
    /// </summary>
    private async Task ApplyCurrentEffectAsync(CancellationToken cancellationToken)
    {
        if (WledEffectComboBox.SelectedIndex < 0 || mainWindow == null) return;

        int fxId = WledEffectComboBox.SelectedIndex;
        int speed = (int)EffectSpeedSlider.Value;
        int intensity = (int)EffectIntensitySlider.Value;
        int paletteId = WledPaletteComboBox.SelectedIndex >= 0 ? WledPaletteComboBox.SelectedIndex : 0;
        int brightness = (int)EffectBrightnessSlider.Value;

        var primaryColor = (EffectPrimaryColorPicker.Color.R, EffectPrimaryColorPicker.Color.G, EffectPrimaryColorPicker.Color.B);
        var secondaryColor = (EffectSecondaryColorPicker.Color.R, EffectSecondaryColorPicker.Color.G, EffectSecondaryColorPicker.Color.B);

        try
        {
            mainWindow.Settings.SelectedWledEffectId = fxId.ToString();
            bool success = await mainWindow.EngineHost.ActivateWledEffectAsync(
                fxId, speed, intensity, paletteId, primaryColor, secondaryColor, brightness, cancellationToken);

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
            // Ignorowane - normalny efekt debounce, nowsze żądanie zastąpiło to.
        }
    }

    /// <summary>
    /// Centralny punkt wywołania efektu z UI. Odczekuje EffectDebounceDelay ciszy
    /// zanim wyśle żądanie HTTP, anulując wcześniejsze żądanie w toku. Zapobiega
    /// to zalewowi requestów podczas przeciągania sliderów lub szybkiej zmiany kolorów.
    /// </summary>
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
        // FIX #5: OnEffectDebounceTick MUSI być metodą instancyjną klasy, nie funkcją
        // lokalną. Funkcja lokalna tworzy nowe zamknięcie (closure) przy każdym wywołaniu
        // tej metody, więc "-=" poniżej nigdy nie trafiało w faktycznie zarejestrowany
        // poprzedni handler - subskrypcje się kumulowały bez końca, a każdy tick timera
        // odpalał WSZYSTKIE nagromadzone handlery naraz (stąd dziesiątki identycznych
        // linii "żądanie anulowane" w tej samej milisekundzie przy każdej zmianie UI).
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
            if (mainWindow.EngineHost.IsRunning)
            {
                mainWindow.EngineHost.Stop();
                ApplyStatus(mainWindow.EngineHost.CurrentStatus);
                UpdateFps();
            }
            else
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(mainWindow);
                await mainWindow.EngineHost.StartAsync(hwnd);
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