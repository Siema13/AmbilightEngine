using System;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AmbilightEngine.Pages;

public sealed partial class CalibrationWizardPage : Page
{
    private enum WizardStep
    {
        Welcome = 0,
        ChannelRed = 1,
        ChannelGreen = 2,
        ChannelBlue = 3,
        Verify = 4
    }

    private enum VerifySource
    {
        White,
        Desktop,
        Video,
        Rainbow
    }

    private const int TotalSteps = 5;
    private const string PromoVideoYouTubeId = "wTcNtgA6gHs";

    private MainWindow? mainWindow;
    private AmbilightSettings? settings;
    private ISettingsApplyService? settingsApplyService;

    private WizardStep currentStep = WizardStep.Welcome;
    private VerifySource currentVerifySource = VerifySource.White;

    // FIX: startuje jako TRUE (nie false!) - Slider w XAML z ustawionym Value="100" wywołuje
    // ValueChanged już podczas parsowania strony, ZANIM Loaded zdąży przypisać settings/
    // mainWindow. Dzięki temu ten "przedwczesny" event jest bezpiecznie ignorowany, a dopiero
    // na końcu Loaded ustawiamy isInitializing = false, odblokowując normalną obsługę.
    private bool isInitializing = true;

    private bool webViewInitialized;

    private double sessionGainR = 1.0;
    private double sessionGainG = 1.0;
    private double sessionGainB = 1.0;

    private int sessionBrightness = 100;
    private double sessionSaturation = 1.0;
    private int sessionKelvin = 6500;
    private double sessionGamma = 2.2;
    private int sessionBlackCutoff = 8;
    private double sessionAttackSpeed = 1.0;
    private double sessionDecaySpeed = 1.0;
    private double sessionPhaseSmoothing = 0.0;

    private readonly DispatcherQueueTimer debounceTimer;

    public CalibrationWizardPage()
    {
        InitializeComponent();

        debounceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        debounceTimer.Interval = TimeSpan.FromMilliseconds(150);
        debounceTimer.IsRepeating = false;
        debounceTimer.Tick += DebounceTimer_Tick;

        Loaded += CalibrationWizardPage_Loaded;
        Unloaded += CalibrationWizardPage_Unloaded;
    }

    private void CalibrationWizardPage_Loaded(object sender, RoutedEventArgs e)
    {
        mainWindow = (Application.Current as App)?.MainAppWindow;
        if (mainWindow is null)
        {
            return;
        }

        settingsApplyService = mainWindow.SettingsApplyService;
        settings = mainWindow.Settings;

        if (settings is null)
        {
            return;
        }

        sessionGainR = settings.ChannelGainR;
        sessionGainG = settings.ChannelGainG;
        sessionGainB = settings.ChannelGainB;

        sessionBrightness = settings.DefaultProfile.BrightnessPercent;
        sessionSaturation = settings.DefaultProfile.SaturationBoost;
        sessionKelvin = settings.DefaultProfile.ColorTemperatureKelvin;
        sessionGamma = settings.DefaultProfile.GammaValue;
        sessionBlackCutoff = settings.DefaultProfile.BlackCutoffThreshold;

        sessionAttackSpeed = settings.MotionAttackSpeed;
        sessionDecaySpeed = settings.MotionDecaySpeed;
        sessionPhaseSmoothing = settings.PhaseSmoothingStrength;

        BuildRainbowGradient();
        ShowStep(WizardStep.Welcome);

        isInitializing = false;
    }

    private void CalibrationWizardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        debounceTimer.Stop();
        StopVideoPlayback();

        if (settings is not null && mainWindow?.EngineHost is not null)
        {
            mainWindow.EngineHost.ApplyLiveSettings();
            mainWindow.EngineHost.ApplyLiveColorCalibration();
        }
    }

    private void ShowStep(WizardStep step)
    {
        currentStep = step;

        WelcomeStepPanel.Visibility = Visibility.Collapsed;
        ColorStepPanel.Visibility = Visibility.Collapsed;
        VerifyStepPanel.Visibility = Visibility.Collapsed;
        ResetStepButton.Visibility = Visibility.Collapsed;

        if (step != WizardStep.Verify)
        {
            StopVideoPlayback();
        }

        StepProgressBar.Value = (int)step + 1;
        StepSubtitleText.Text = $"Krok {(int)step + 1} z {TotalSteps} — {GetStepName(step)}";

        BackButton.IsEnabled = step != WizardStep.Welcome;
        NextButton.Content = step == WizardStep.Verify ? "Zapisz i zakończ" : "Dalej";

        switch (step)
        {
            case WizardStep.Welcome:
                WelcomeStepPanel.Visibility = Visibility.Visible;
                break;

            case WizardStep.ChannelRed:
                ConfigureColorStep(
                    swatchColor: Color.FromArgb(255, 255, 0, 0),
                    label: "Kanał R (%)",
                    instruction: "Na ekranie widzisz pełną czerwień. Dostrój suwak, aż kolor diod LED będzie identyczny.",
                    currentValue: sessionGainR);
                break;

            case WizardStep.ChannelGreen:
                ConfigureColorStep(
                    swatchColor: Color.FromArgb(255, 0, 255, 0),
                    label: "Kanał G (%)",
                    instruction: "Na ekranie widzisz pełną zieleń. Dostrój suwak, aż kolor diod LED będzie identyczny.",
                    currentValue: sessionGainG);
                break;

            case WizardStep.ChannelBlue:
                ConfigureColorStep(
                    swatchColor: Color.FromArgb(255, 0, 0, 255),
                    label: "Kanał B (%)",
                    instruction: "Na ekranie widzisz pełny niebieski. Dostrój suwak, aż kolor diod LED będzie identyczny.",
                    currentValue: sessionGainB);
                break;

            case WizardStep.Verify:
                VerifyStepPanel.Visibility = Visibility.Visible;
                InitializeWizardParameterControls();
                ApplyAllLiveWizardSettings();
                ApplyVerifySource(currentVerifySource);
                break;
        }
    }

    private static string GetStepName(WizardStep step) => step switch
    {
        WizardStep.Welcome => "przygotowanie",
        WizardStep.ChannelRed => "kanał czerwony",
        WizardStep.ChannelGreen => "kanał zielony",
        WizardStep.ChannelBlue => "kanał niebieski",
        WizardStep.Verify => "dokrawanie i weryfikacja",
        _ => string.Empty
    };

    private void ConfigureColorStep(Color swatchColor, string label, string instruction, double currentValue)
    {
        bool wasInitializing = isInitializing;
        isInitializing = true;

        ColorStepPanel.Visibility = Visibility.Visible;
        ResetStepButton.Visibility = Visibility.Visible;

        ColorSwatchBorder.Background = new SolidColorBrush(swatchColor);
        ColorStepSliderLabel.Text = label;
        ColorStepInstructionText.Text = instruction;

        int percent = (int)Math.Round(currentValue * 100.0);
        ColorStepSlider.Value = percent;
        ColorStepValueText.Text = percent.ToString();

        isInitializing = wasInitializing;

        ApplyAllLiveWizardSettings();
    }

    private void ColorStepSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        ColorStepValueText.Text = percent.ToString();
        double gain = percent / 100.0;

        switch (currentStep)
        {
            case WizardStep.ChannelRed:
                sessionGainR = gain;
                break;
            case WizardStep.ChannelGreen:
                sessionGainG = gain;
                break;
            case WizardStep.ChannelBlue:
                sessionGainB = gain;
                break;
        }

        ApplyAllLiveWizardSettings();

        debounceTimer.Stop();
        debounceTimer.Start();
    }

    private void ResetStepButton_Click(object sender, RoutedEventArgs e)
    {
        ColorStepSlider.Value = 100;
    }

    private void InitializeWizardParameterControls()
    {
        bool wasInitializing = isInitializing;
        isInitializing = true;

        WizardBrightnessSlider.Value = sessionBrightness;
        WizardBrightnessValueText.Text = sessionBrightness.ToString();

        int saturationPercent = (int)Math.Round(sessionSaturation * 100.0);
        WizardSaturationSlider.Value = saturationPercent;
        WizardSaturationValueText.Text = saturationPercent.ToString();

        WizardKelvinSlider.Value = sessionKelvin;
        WizardKelvinValueText.Text = sessionKelvin.ToString();

        WizardGammaSlider.Value = sessionGamma * 10.0;
        WizardGammaValueText.Text = sessionGamma.ToString("0.0");

        WizardBlackCutoffSlider.Value = sessionBlackCutoff;
        WizardBlackCutoffValueText.Text = sessionBlackCutoff.ToString();

        WizardAttackSlider.Value = sessionAttackSpeed;
        WizardAttackValueText.Text = sessionAttackSpeed.ToString("0.0");

        WizardDecaySlider.Value = sessionDecaySpeed;
        WizardDecayValueText.Text = sessionDecaySpeed.ToString("0.0");

        int phaseSmoothingPercent = (int)Math.Round(sessionPhaseSmoothing * 100.0);
        WizardPhaseSmoothingSlider.Value = phaseSmoothingPercent;
        WizardPhaseSmoothingValueText.Text = phaseSmoothingPercent.ToString();

        isInitializing = wasInitializing;
    }

    private void WizardParameterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        if (ReferenceEquals(sender, WizardBrightnessSlider))
        {
            sessionBrightness = (int)Math.Round(e.NewValue);
            WizardBrightnessValueText.Text = sessionBrightness.ToString();
        }
        else if (ReferenceEquals(sender, WizardSaturationSlider))
        {
            int percent = (int)Math.Round(e.NewValue);
            sessionSaturation = percent / 100.0;
            WizardSaturationValueText.Text = percent.ToString();
        }
        else if (ReferenceEquals(sender, WizardKelvinSlider))
        {
            sessionKelvin = (int)Math.Round(e.NewValue);
            WizardKelvinValueText.Text = sessionKelvin.ToString();
        }
        else if (ReferenceEquals(sender, WizardGammaSlider))
        {
            sessionGamma = Math.Round(e.NewValue / 10.0, 1);
            WizardGammaValueText.Text = sessionGamma.ToString("0.0");
        }
        else if (ReferenceEquals(sender, WizardBlackCutoffSlider))
        {
            sessionBlackCutoff = (int)Math.Round(e.NewValue);
            WizardBlackCutoffValueText.Text = sessionBlackCutoff.ToString();
        }
        else if (ReferenceEquals(sender, WizardAttackSlider))
        {
            sessionAttackSpeed = Math.Round(e.NewValue, 1);
            WizardAttackValueText.Text = sessionAttackSpeed.ToString("0.0");
        }
        else if (ReferenceEquals(sender, WizardDecaySlider))
        {
            sessionDecaySpeed = Math.Round(e.NewValue, 1);
            WizardDecayValueText.Text = sessionDecaySpeed.ToString("0.0");
        }
        else if (ReferenceEquals(sender, WizardPhaseSmoothingSlider))
        {
            int percent = (int)Math.Round(e.NewValue);
            sessionPhaseSmoothing = percent / 100.0;
            WizardPhaseSmoothingValueText.Text = percent.ToString();
        }

        ApplyAllLiveWizardSettings();

        debounceTimer.Stop();
        debounceTimer.Start();
    }

    private void WizardResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (settings is null)
        {
            return;
        }

        sessionBrightness = 100;
        sessionSaturation = 1.0;
        sessionKelvin = 6500;
        sessionGamma = 2.2;
        sessionBlackCutoff = 8;
        sessionAttackSpeed = 1.0;
        sessionDecaySpeed = 1.0;
        sessionPhaseSmoothing = 0.0;

        InitializeWizardParameterControls();
        ApplyAllLiveWizardSettings();

        debounceTimer.Stop();
        debounceTimer.Start();
    }

    private void DebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        PersistSessionSettingsWithoutLeavingWizard();
    }

    private void VerifySourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (isInitializing)
        {
            return;
        }

        if (ReferenceEquals(sender, VerifySourceWhiteRadio))
        {
            ApplyVerifySource(VerifySource.White);
        }
        else if (ReferenceEquals(sender, VerifySourceDesktopRadio))
        {
            ApplyVerifySource(VerifySource.Desktop);
        }
        else if (ReferenceEquals(sender, VerifySourceVideoRadio))
        {
            ApplyVerifySource(VerifySource.Video);
        }
        else if (ReferenceEquals(sender, VerifySourceRainbowRadio))
        {
            ApplyVerifySource(VerifySource.Rainbow);
        }
    }

    private void ApplyVerifySource(VerifySource source)
    {
        currentVerifySource = source;

        VerifyWhiteSwatch.Visibility = Visibility.Collapsed;
        VerifyDesktopPanel.Visibility = Visibility.Collapsed;
        VerifyVideoWebView.Visibility = Visibility.Collapsed;
        VerifyRainbowSwatch.Visibility = Visibility.Collapsed;

        StopVideoPlayback();

        switch (source)
        {
            case VerifySource.White:
                VerifyWhiteSwatch.Visibility = Visibility.Visible;
                VerifyInstructionText.Text = "Sprawdź, czy biały kolor na ekranie i na diodach LED wygląda neutralnie (bez przebarwień na żółto, niebiesko czy zielono).";
                break;

            case VerifySource.Desktop:
                VerifyDesktopPanel.Visibility = Visibility.Visible;
                VerifyInstructionText.Text = "Przechwytywanie pulpitu działa normalnie w tle. Przełącz się na inne okno i obserwuj reakcję diod LED na realną treść.";
                break;

            case VerifySource.Video:
                VerifyInstructionText.Text = "Odtwarzany materiał referencyjny (GoPro HERO4) pomaga ocenić kalibrację na zmiennej, kolorowej treści filmowej.";
                StartVideoPlayback();
                break;

            case VerifySource.Rainbow:
                VerifyRainbowSwatch.Visibility = Visibility.Visible;
                VerifyInstructionText.Text = "Klasyczny pasek tęczy - dobry test do sprawdzenia płynności przejść kolorów na całym zakresie widma.";
                break;
        }
    }

    private async void StartVideoPlayback()
    {
        try
        {
            if (!webViewInitialized)
            {
                await VerifyVideoWebView.EnsureCoreWebView2Async();
                webViewInitialized = true;
            }

            string embedUrl = $"https://www.youtube.com/embed/{PromoVideoYouTubeId}?autoplay=1&loop=1&playlist={PromoVideoYouTubeId}&mute=1";
            VerifyVideoWebView.Source = new Uri(embedUrl);
            VerifyVideoWebView.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            VerifyInstructionText.Text = $"Nie udało się załadować materiału wideo: {ex.Message}. Upewnij się, że masz połączenie z internetem i zainstalowany WebView2 Runtime.";
            VerifyVideoWebView.Visibility = Visibility.Collapsed;
        }
    }

    private void StopVideoPlayback()
    {
        if (webViewInitialized)
        {
            VerifyVideoWebView.Source = new Uri("about:blank");
        }
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

            gradientBrush.GradientStops.Add(new GradientStop
            {
                Color = color,
                Offset = i / (double)(stopCount - 1)
            });
        }

        VerifyRainbowSwatch.Background = gradientBrush;
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

        return Color.FromArgb(
            255,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private void ApplyAllLiveWizardSettings()
    {
        if (settings is null || mainWindow?.EngineHost is null)
        {
            return;
        }

        settings.ChannelGainR = sessionGainR;
        settings.ChannelGainG = sessionGainG;
        settings.ChannelGainB = sessionGainB;

        settings.DefaultProfile.BrightnessPercent = sessionBrightness;
        settings.DefaultProfile.SaturationBoost = sessionSaturation;
        settings.DefaultProfile.ColorTemperatureKelvin = sessionKelvin;
        settings.DefaultProfile.GammaValue = sessionGamma;
        settings.DefaultProfile.BlackCutoffThreshold = sessionBlackCutoff;

        settings.MotionAttackSpeed = sessionAttackSpeed;
        settings.MotionDecaySpeed = sessionDecaySpeed;
        settings.PhaseSmoothingStrength = sessionPhaseSmoothing;

        mainWindow.EngineHost.ApplyLiveSettings();
        mainWindow.EngineHost.ApplyLiveColorCalibration();
    }

    private void PersistSessionSettingsWithoutLeavingWizard()
    {
        if (settings is null || settingsApplyService is null)
        {
            return;
        }

        settings.ChannelGainR = sessionGainR;
        settings.ChannelGainG = sessionGainG;
        settings.ChannelGainB = sessionGainB;

        settings.DefaultProfile.BrightnessPercent = sessionBrightness;
        settings.DefaultProfile.SaturationBoost = sessionSaturation;
        settings.DefaultProfile.ColorTemperatureKelvin = sessionKelvin;
        settings.DefaultProfile.GammaValue = sessionGamma;
        settings.DefaultProfile.BlackCutoffThreshold = sessionBlackCutoff;

        settings.MotionAttackSpeed = sessionAttackSpeed;
        settings.MotionDecaySpeed = sessionDecaySpeed;
        settings.PhaseSmoothingStrength = sessionPhaseSmoothing;

        settingsApplyService.SaveAndApplyImage(settings);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentStep == WizardStep.Welcome)
        {
            return;
        }

        ShowStep((WizardStep)((int)currentStep - 1));
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentStep == WizardStep.Verify)
        {
            FinishWizard();
            return;
        }

        ShowStep((WizardStep)((int)currentStep + 1));
    }

    private void FinishWizard()
    {
        debounceTimer.Stop();
        StopVideoPlayback();
        PersistSessionSettingsWithoutLeavingWizard();

        if (settings is not null && settingsApplyService is not null)
        {
            settings.HasCompletedCalibrationOnboarding = true;
            settingsApplyService.SaveAndApplyImage(settings);
        }

        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void CancelWizardButton_Click(object sender, RoutedEventArgs e)
    {
        debounceTimer.Stop();
        StopVideoPlayback();

        if (settings is not null && mainWindow?.EngineHost is not null)
        {
            mainWindow.EngineHost.ApplyLiveSettings();
            mainWindow.EngineHost.ApplyLiveColorCalibration();
        }

        if (settings is not null && settingsApplyService is not null)
        {
            settings.HasCompletedCalibrationOnboarding = true;
            settingsApplyService.SaveAndApplyImage(settings);
        }

        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}