using System;
using System.Runtime.InteropServices;
using AmbilightEngine.Core.Capture;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using WinRT.Interop;

namespace AmbilightEngine;

public sealed partial class CalibrationOverlayWindow : Window
{
    private enum VerifySource { White, Desktop, Video, Rainbow }

    // NOWOŚĆ: ścieżka do LOKALNEGO pliku wideo (nie YouTube) - eliminuje całkowicie
    // problemy z embedowaniem/referrer-policy/blokadami YouTube (Error 153), korzystając
    // z natywnego MediaPlayerElement (Windows Media Foundation). Podmień na dowolny plik
    // .mp4 na dysku - np. skopiowany lokalnie materiał promocyjny.
    private const string LocalVideoPath = @"C:\AmbilightAssets\calibration_demo.mp4";

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    private readonly MainWindow ownerWindow;
    private readonly AmbilightSettings settings;
    private readonly ISettingsApplyService settingsApplyService;

    private VerifySource currentSource = VerifySource.White;
    private bool isInitializing = true;
    private bool isPanelVisible;
    private bool isClosingWithoutSave;
    private DesktopPeekWindow? peekAnchor;
    private MediaPlayer? mediaPlayer;

    private double sessionGainR, sessionGammaR, sessionOffsetR;
    private double sessionGainG, sessionGammaG, sessionOffsetG;
    private double sessionGainB, sessionGammaB, sessionOffsetB;

    public CalibrationOverlayWindow(MainWindow owner)
    {
        InitializeComponent();
        isInitializing = true;

        ownerWindow = owner;
        settings = owner.Settings;
        settingsApplyService = owner.SettingsApplyService;

        sessionGainR = settings.ChannelGainR; sessionGammaR = settings.ChannelGammaR; sessionOffsetR = settings.ChannelOffsetR;
        sessionGainG = settings.ChannelGainG; sessionGammaG = settings.ChannelGammaG; sessionOffsetG = settings.ChannelOffsetG;
        sessionGainB = settings.ChannelGainB; sessionGammaB = settings.ChannelGammaB; sessionOffsetB = settings.ChannelOffsetB;

        LoadSlidersFromSession();
        BuildRainbowGradient();

        ColorSwatch.Background = new SolidColorBrush(Colors.White);

        Activated += CalibrationOverlayWindow_Activated;
        Closed += CalibrationOverlayWindow_Closed;
        RootGrid.KeyDown += RootGrid_KeyDown;

        PreventIdleSleep();
    }

    private void PreventIdleSleep() => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
    private void RestoreNormalIdleBehavior() => SetThreadExecutionState(ES_CONTINUOUS);

    private async void CalibrationOverlayWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= CalibrationOverlayWindow_Activated;
        await System.Threading.Tasks.Task.Delay(50);
        MoveToCapturedMonitorAndGoFullScreen();
        isInitializing = false;
        ShowWelcome();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void MoveToCapturedMonitorAndGoFullScreen()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            IntPtr ownerHwnd = WindowNative.GetWindowHandle(ownerWindow);

            IntPtr targetMonitor = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(settings.SelectedMonitorDeviceId))
            {
                targetMonitor = MonitorCaptureHelper.FindMonitorHandleByDeviceName(settings.SelectedMonitorDeviceId);
            }
            if (targetMonitor == IntPtr.Zero)
            {
                targetMonitor = MonitorCaptureHelper.GetPrimaryMonitorHandle(ownerHwnd);
            }

            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            DisplayArea? targetDisplayArea = null;
            foreach (var displayArea in DisplayArea.FindAll())
            {
                if (displayArea.DisplayId.Value == unchecked((ulong)targetMonitor.ToInt64()))
                {
                    targetDisplayArea = displayArea;
                    break;
                }
            }
            targetDisplayArea ??= DisplayArea.Primary;

            appWindow.MoveAndResize(targetDisplayArea.WorkArea);
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch (Exception)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }

    // ── Powitanie / panel ────────────────────────────────────────────────────

    private void ShowWelcome()
    {
        WelcomePanel.Visibility = Visibility.Visible;
        MainPanel.Visibility = Visibility.Collapsed;
        ExpandHintButton.Visibility = Visibility.Collapsed;
        ColorSwatch.Visibility = Visibility.Visible;
        ColorSwatch.Background = new SolidColorBrush(Color.FromArgb(255, 20, 20, 20));
        RainbowSwatch.Visibility = Visibility.Collapsed;
        StopVideoPlayback();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        WelcomePanel.Visibility = Visibility.Collapsed;
        ShowPanel();
        ApplyVerifySource(currentSource);
        ApplyLivePreview();
    }

    private void ToggleDrawer_Click(object sender, RoutedEventArgs e)
    {
        if (isPanelVisible) HidePanel(); else ShowPanel();
    }

    private void ShowPanel()
    {
        isPanelVisible = true;
        MainPanel.Visibility = Visibility.Visible;
        ExpandHintButton.Visibility = Visibility.Collapsed;
    }

    private void HidePanel()
    {
        isPanelVisible = false;
        MainPanel.Visibility = Visibility.Collapsed;
        ExpandHintButton.Visibility = Visibility.Visible;
    }

    // ── Kolor testowy (dla źródła Biały) ─────────────────────────────────────

    private void RgbTestColorRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (isInitializing || currentSource != VerifySource.White) return;

        if (ReferenceEquals(sender, RgbTestBlackRadio)) ColorSwatch.Background = new SolidColorBrush(Colors.Black);
        else if (ReferenceEquals(sender, RgbTestWhiteRadio)) ColorSwatch.Background = new SolidColorBrush(Colors.White);
        else if (ReferenceEquals(sender, RgbTestGrayRadio)) ColorSwatch.Background = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
        else if (ReferenceEquals(sender, RgbTestRedRadio)) ColorSwatch.Background = new SolidColorBrush(Color.FromArgb(255, 255, 0, 0));
        else if (ReferenceEquals(sender, RgbTestGreenRadio)) ColorSwatch.Background = new SolidColorBrush(Color.FromArgb(255, 0, 255, 0));
        else if (ReferenceEquals(sender, RgbTestBlueRadio)) ColorSwatch.Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 255));
    }

    private object GetCheckedTestColorRadio()
    {
        if (RgbTestBlackRadio.IsChecked == true) return RgbTestBlackRadio;
        if (RgbTestGrayRadio.IsChecked == true) return RgbTestGrayRadio;
        if (RgbTestRedRadio.IsChecked == true) return RgbTestRedRadio;
        if (RgbTestGreenRadio.IsChecked == true) return RgbTestGreenRadio;
        if (RgbTestBlueRadio.IsChecked == true) return RgbTestBlueRadio;
        return RgbTestWhiteRadio;
    }

    // ── 9 sliderów RGB ────────────────────────────────────────────────────────

    private void LoadSlidersFromSession()
    {
        isInitializing = true;

        GainRSlider.Value = sessionGainR * 100.0; GammaRSlider.Value = sessionGammaR * 10.0; OffsetRSlider.Value = sessionOffsetR * 100.0;
        GainGSlider.Value = sessionGainG * 100.0; GammaGSlider.Value = sessionGammaG * 10.0; OffsetGSlider.Value = sessionOffsetG * 100.0;
        GainBSlider.Value = sessionGainB * 100.0; GammaBSlider.Value = sessionGammaB * 10.0; OffsetBSlider.Value = sessionOffsetB * 100.0;

        UpdateAllValueTexts();
        isInitializing = false;
    }

    private void UpdateAllValueTexts()
    {
        GainRValueText.Text = ((int)Math.Round(sessionGainR * 100.0)).ToString();
        GammaRValueText.Text = sessionGammaR.ToString("0.0");
        OffsetRValueText.Text = ((int)Math.Round(sessionOffsetR * 100.0)).ToString();

        GainGValueText.Text = ((int)Math.Round(sessionGainG * 100.0)).ToString();
        GammaGValueText.Text = sessionGammaG.ToString("0.0");
        OffsetGValueText.Text = ((int)Math.Round(sessionOffsetG * 100.0)).ToString();

        GainBValueText.Text = ((int)Math.Round(sessionGainB * 100.0)).ToString();
        GammaBValueText.Text = sessionGammaB.ToString("0.0");
        OffsetBValueText.Text = ((int)Math.Round(sessionOffsetB * 100.0)).ToString();
    }

    private void ChannelSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing) return;

        if (ReferenceEquals(sender, GainRSlider)) sessionGainR = e.NewValue / 100.0;
        else if (ReferenceEquals(sender, GammaRSlider)) sessionGammaR = e.NewValue / 10.0;
        else if (ReferenceEquals(sender, OffsetRSlider)) sessionOffsetR = e.NewValue / 100.0;
        else if (ReferenceEquals(sender, GainGSlider)) sessionGainG = e.NewValue / 100.0;
        else if (ReferenceEquals(sender, GammaGSlider)) sessionGammaG = e.NewValue / 10.0;
        else if (ReferenceEquals(sender, OffsetGSlider)) sessionOffsetG = e.NewValue / 100.0;
        else if (ReferenceEquals(sender, GainBSlider)) sessionGainB = e.NewValue / 100.0;
        else if (ReferenceEquals(sender, GammaBSlider)) sessionGammaB = e.NewValue / 10.0;
        else if (ReferenceEquals(sender, OffsetBSlider)) sessionOffsetB = e.NewValue / 100.0;

        UpdateAllValueTexts();
        ApplyLivePreview();
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        sessionGainR = 1.0; sessionGammaR = 1.0; sessionOffsetR = 0.0;
        sessionGainG = 1.0; sessionGammaG = 1.0; sessionOffsetG = 0.0;
        sessionGainB = 1.0; sessionGammaB = 1.0; sessionOffsetB = 0.0;

        LoadSlidersFromSession();
        ApplyLivePreview();
    }

    private void ApplyLivePreview()
    {
        settings.ChannelGainR = sessionGainR; settings.ChannelGammaR = sessionGammaR; settings.ChannelOffsetR = sessionOffsetR;
        settings.ChannelGainG = sessionGainG; settings.ChannelGammaG = sessionGammaG; settings.ChannelOffsetG = sessionOffsetG;
        settings.ChannelGainB = sessionGainB; settings.ChannelGammaB = sessionGammaB; settings.ChannelOffsetB = sessionOffsetB;

        ownerWindow.EngineHost.ApplyLiveSettings();
    }

    // ── Źródło podglądu (zawsze wybieralne, niezależnie od sliderów) ─────────

    private void SourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        if (ReferenceEquals(sender, SourceWhiteRadio)) ApplyVerifySource(VerifySource.White);
        else if (ReferenceEquals(sender, SourceDesktopRadio)) ApplyVerifySource(VerifySource.Desktop);
        else if (ReferenceEquals(sender, SourceVideoRadio)) ApplyVerifySource(VerifySource.Video);
        else if (ReferenceEquals(sender, SourceRainbowRadio)) ApplyVerifySource(VerifySource.Rainbow);
    }

    private void ApplyVerifySource(VerifySource source)
    {
        currentSource = source;

        ColorSwatch.Visibility = Visibility.Collapsed;
        RainbowSwatch.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        TestColorPicker.Visibility = Visibility.Collapsed;
        StopVideoPlayback();

        switch (source)
        {
            case VerifySource.White:
                ColorSwatch.Visibility = Visibility.Visible;
                TestColorPicker.Visibility = Visibility.Visible;
                RgbTestColorRadio_Checked(GetCheckedTestColorRadio(), new RoutedEventArgs());
                break;

            case VerifySource.Desktop:
                MinimizeForDesktopPeek();
                break;

            case VerifySource.Video:
                StartVideoPlayback();
                break;

            case VerifySource.Rainbow:
                RainbowSwatch.Visibility = Visibility.Visible;
                break;
        }
    }

   
    private void MinimizeForDesktopPeek()
    {
        peekAnchor ??= new DesktopPeekWindow(this);
        peekAnchor.Activate();

        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        if (AppWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Minimize();
        }
    }

    internal void RestoreFromDesktopPeek()
    {
        peekAnchor?.Close();
        peekAnchor = null;

        MoveToCapturedMonitorAndGoFullScreen();
        RootGrid.Focus(FocusState.Programmatic);
    }

    // ── Wideo lokalne (MediaPlayerElement, bez YouTube/WebView2) ─────────────

    private void StartVideoPlayback()
    {
        try
        {
            if (!System.IO.File.Exists(LocalVideoPath))
            {
                ErrorDiagnosticsText.Text =
                    $"Nie znaleziono pliku wideo:\n{LocalVideoPath}\n\nUmieść plik .mp4 w tej lokalizacji lub zmień ścieżkę LocalVideoPath w kodzie.";
                ErrorDiagnosticsText.Visibility = Visibility.Visible;
                return;
            }

            mediaPlayer ??= new MediaPlayer { IsLoopingEnabled = true };
            mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(LocalVideoPath));

            VideoPlayer.SetMediaPlayer(mediaPlayer);
            VideoPlayer.Visibility = Visibility.Visible;
            mediaPlayer.Play();

            // NOWOŚĆ: pokaż od razu pasek sterowania (play/pauza + scrubber),
            // żeby użytkownik widział, że wideo można kontrolować, bez konieczności
            // ruszania myszą, by wywołać auto-show.
            VideoPlayer.TransportControls.Show();
        }
        catch (Exception ex)
        {
            ErrorDiagnosticsText.Text = $"BŁĄD odtwarzania wideo:\n{ex.GetType().Name}\n{ex.Message}";
            ErrorDiagnosticsText.Visibility = Visibility.Visible;
        }
    }

    private void StopVideoPlayback()
    {
        ErrorDiagnosticsText.Visibility = Visibility.Collapsed;
        mediaPlayer?.Pause();
    }

    private void BuildRainbowGradient()
    {
        var gradientBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0)
        };

        const int stopCount = 12;
        for (int i = 0; i < stopCount; i++)
        {
            double hue = i / (double)(stopCount - 1) * 300.0;
            Color color = HsvToRgb(hue, 1.0, 1.0);
            gradientBrush.GradientStops.Add(new GradientStop { Color = color, Offset = i / (double)(stopCount - 1) });
        }

        RainbowSwatch.Background = gradientBrush;
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = value - c;
        double r, g, b;

        if (hue < 60) { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromArgb(255, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ── Sterowanie klawiaturą ────────────────────────────────────────────────

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                isClosingWithoutSave = false;
                FinishAndClose();
                break;

            case Windows.System.VirtualKey.F9:
                ToggleDrawer_Click(this, new RoutedEventArgs());
                break;

            case Windows.System.VirtualKey.Number1:
                SourceWhiteRadio.IsChecked = true;
                break;
            case Windows.System.VirtualKey.Number2:
                SourceDesktopRadio.IsChecked = true;
                break;
            case Windows.System.VirtualKey.Number3:
                SourceVideoRadio.IsChecked = true;
                break;
            case Windows.System.VirtualKey.Number4:
                SourceRainbowRadio.IsChecked = true;
                break;
        }
    }

    // ── Zapis i zamknięcie ───────────────────────────────────────────────────

    private void FinishButton_Click(object sender, RoutedEventArgs e) => FinishAndClose();

    private void FinishAndClose()
    {
        ApplyLivePreview();
        settings.HasCompletedCalibrationOnboarding = true;
        settingsApplyService.SaveAndApplyImage(settings);

        // FIX: musimy w pełni odłączyć i zwolnić MediaPlayer PRZED Close() - jeśli
        // Dispose() następuje w handlerze Closed (czyli już w trakcie destrukcji
        // okna), WinRT bywa w stanie, gdzie operacje COM na MediaPlayer kończą się
        // COMException 0x80004004 (E_ABORT), bo silnik Media Foundation jest już
        // częściowo ubity razem z oknem.
        DisposeMediaPlayerSafely();

        Close();
    }

    private void DisposeMediaPlayerSafely()
    {
        if (mediaPlayer is null) return;

        try
        {
            VideoPlayer.SetMediaPlayer(null);
            mediaPlayer.Pause();
            mediaPlayer.Dispose();
        }
        catch (Exception)
        {
            // Best-effort - zwolnienie zasobów multimedialnych nie może zablokować zamknięcia okna.
        }
        finally
        {
            mediaPlayer = null;
        }
    }

    private void CalibrationOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        RestoreNormalIdleBehavior();

        peekAnchor?.Close();
        StopVideoPlayback();
        mediaPlayer?.Dispose();
    }
}