using System;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using AmbilightEngine.Core.Models;
namespace AmbilightEngine.Pages;

public sealed partial class SettingsImagePage : Page
{
    private MainWindow? mainWindow;
    private AmbilightSettings? settings;
    private ISettingsApplyService? settingsApplyService;
    private bool isInitializing;
    private Color wallColor = Color.FromArgb(255, 128, 128, 128);

    public SettingsImagePage()
    {
        InitializeComponent();
        Loaded += SettingsImagePage_Loaded;
    }

    private void SaveCurrentImageSettingsAsDefaultButton_Click(
    object sender,
    RoutedEventArgs e)
    {
        if (settings is null || mainWindow is null)
        {
            DefaultProfileSaveStatusText.Text =
                "Ustawienia nie są jeszcze gotowe. Otwórz stronę ponownie i spróbuj ponownie.";

            return;
        }

        try
        {
            AppProfile defaultProfile = settings.DefaultProfile ?? new AppProfile();

            defaultProfile.DisplayName = "Domyślny";
            defaultProfile.ExecutableFileName = string.Empty;
            defaultProfile.AllowBackgroundActivation = false;
            defaultProfile.Priority = 0;
            defaultProfile.IsBuiltInDefault = true;

            // To jest profil „Parametry obrazu”, czyli zachowanie Video Sync.
            defaultProfile.ActionType = ProfileActionType.ImageDsp;

            // Suwaki mają różne skale niż właściwości AppProfile:
            // Saturation: 0–300% -> 0.0–3.0.
            // Gamma: wartość suwaka jest mnożona x10.
            defaultProfile.BrightnessPercent =
                (int)Math.Round(BrightnessSlider.Value);

            defaultProfile.SaturationBoost =
                Math.Round(SaturationSlider.Value / 100.0, 2);

            defaultProfile.ColorTemperatureKelvin =
                (int)Math.Round(KelvinSlider.Value);

            defaultProfile.BlackCutoffThreshold =
                (int)Math.Round(BlackCutoffSlider.Value);

            defaultProfile.GammaValue =
                Math.Round(GammaSlider.Value / 10.0, 1);

            // W tej stronie nie ma osobnego suwaka „SmoothingSlider”.
            // Ustawiamy wartość z aktualnego profilu domyślnego, ponieważ to jest
            // parametr profilu, a Attack/Decay są globalnymi parametrami silnika.
            defaultProfile.SmoothingSpeedMs =
                settings.DefaultProfile.SmoothingSpeedMs;

            settings.DefaultProfile = defaultProfile;

            // Zapis na dysk + przekazanie bieżących ustawień do silnika.
            settingsApplyService?.SaveAndApplyImage(settings);

            // Przebudowuje listę obserwowaną przez ProcessProfileWatcher i powoduje,
            // że nowy profil domyślny będzie używany jako fallback.
            mainWindow.EngineHost.RefreshProfileList();

            DefaultProfileSaveStatusText.Text =
                "Zapisano bieżące parametry obrazu jako profil domyślny Video Sync.";
        }
        catch (Exception ex)
        {
            DefaultProfileSaveStatusText.Text =
                $"Nie udało się zapisać profilu domyślnego: {ex.Message}";
        }
    }
    private void SettingsImagePage_Loaded(object sender, RoutedEventArgs e)
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

        isInitializing = true;

        LedCountBox.Value = settings.LedCount;
        SamplingDepthSlider.Value = settings.SamplingDepth;
        SamplingDepthValueText.Text = settings.SamplingDepth.ToString();

        BrightnessSlider.Value = settings.DefaultProfile.BrightnessPercent;
        BrightnessValueText.Text = settings.DefaultProfile.BrightnessPercent.ToString();

        SaturationSlider.Value = settings.DefaultProfile.SaturationBoost * 100.0;
        SaturationValueText.Text = ((int)Math.Round(settings.DefaultProfile.SaturationBoost * 100.0)).ToString();

        KelvinSlider.Value = settings.DefaultProfile.ColorTemperatureKelvin;
        KelvinValueText.Text = settings.DefaultProfile.ColorTemperatureKelvin.ToString();

        BlackCutoffSlider.Value = settings.DefaultProfile.BlackCutoffThreshold;
        BlackCutoffValueText.Text = settings.DefaultProfile.BlackCutoffThreshold.ToString();

        GammaSlider.Value = settings.DefaultProfile.GammaValue * 10.0;
        GammaValueText.Text = settings.DefaultProfile.GammaValue.ToString("0.0");

        // Reakcja przechwytywania – inicjalizacja suwaków z ustawień
        AttackSpeedSlider.Value = settings.MotionAttackSpeed;
        AttackSpeedValueText.Text = settings.MotionAttackSpeed.ToString("0.0");

        DecaySpeedSlider.Value = settings.MotionDecaySpeed;
        DecaySpeedValueText.Text = settings.MotionDecaySpeed.ToString("0.0");

        ColorSensitivitySlider.Value = settings.ColorSensitivity;
        ColorSensitivityValueText.Text = settings.ColorSensitivity.ToString("0.0");

        MinBrightnessSlider.Value = settings.MinimumBrightnessFloor;
        MinBrightnessValueText.Text = settings.MinimumBrightnessFloor.ToString();

        // Peak-blend, shadow boost, noise floor, edge feather
        int peakWeightPercent = (int)Math.Round(settings.ZonePeakWeight * 100.0);
        ZonePeakWeightSlider.Value = peakWeightPercent;
        ZonePeakWeightValueText.Text = peakWeightPercent.ToString();

        ShadowBoostSlider.Value = settings.ShadowBoostStrength * 10.0;
        ShadowBoostValueText.Text = settings.ShadowBoostStrength.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        NoiseFloorSlider.Value = settings.NoiseFloor;
        NoiseFloorValueText.Text = settings.NoiseFloor.ToString();

        EdgeFeatherSlider.Value = settings.EdgeFeatherPixels;
        EdgeFeatherValueText.Text = settings.EdgeFeatherPixels.ToString();

        // NOWOŚĆ: wygładzanie fazowe i kalibracja per-kanał RGB
        int phaseSmoothingPercent = (int)Math.Round(settings.PhaseSmoothingStrength * 100.0);
        PhaseSmoothingSlider.Value = phaseSmoothingPercent;
        PhaseSmoothingValueText.Text = phaseSmoothingPercent.ToString();

        int channelGainRPercent = (int)Math.Round(settings.ChannelGainR * 100.0);
        ChannelGainRSlider.Value = channelGainRPercent;
        ChannelGainRValueText.Text = channelGainRPercent.ToString();

        int channelGainGPercent = (int)Math.Round(settings.ChannelGainG * 100.0);
        ChannelGainGSlider.Value = channelGainGPercent;
        ChannelGainGValueText.Text = channelGainGPercent.ToString();

        int channelGainBPercent = (int)Math.Round(settings.ChannelGainB * 100.0);
        ChannelGainBSlider.Value = channelGainBPercent;
        ChannelGainBValueText.Text = channelGainBPercent.ToString();

        BlackBarToggle.IsOn = settings.EnableBlackBarDetection;
        BlackBarThresholdSlider.Value = settings.BlackBarThreshold;
        BlackBarThresholdValueText.Text = settings.BlackBarThreshold.ToString();

        int minRatioPercent = (int)Math.Round(settings.BlackBarMinRatio * 100.0);
        BlackBarMinRatioSlider.Value = minRatioPercent;
        BlackBarMinRatioValueText.Text = minRatioPercent.ToString();

        BlackBarPanel.Opacity = settings.EnableBlackBarDetection ? 1.0 : 0.45;
        BlackBarPanel.IsHitTestVisible = settings.EnableBlackBarDetection;

        LoadWallColorSettings();

        isInitializing = false;
    }

    private void LoadWallColorSettings()
    {
        if (settings is null)
        {
            return;
        }

        bool enabled = !string.IsNullOrWhiteSpace(settings.WallColorHex);
        WallColorToggle.IsOn = enabled;

        if (enabled)
        {
            wallColor = HexToColor(settings.WallColorHex!);
        }

        UpdateWallColorPreview(wallColor);

        int strengthPercent = (int)Math.Round(Math.Clamp(settings.WallColorStrength, 0f, 1f) * 100f);
        WallStrengthSlider.Value = strengthPercent;
        WallStrengthValueText.Text = strengthPercent.ToString();

        SetWallPanelEnabled(enabled);
    }

    private void SaveCaptureSettings()
    {
        if (settings is null || settingsApplyService is null)
        {
            return;
        }

        settingsApplyService.SaveAndApplyGeometry(settings);
    }

    private void SaveImageSettings()
    {
        if (settings is null || settingsApplyService is null)
        {
            return;
        }

        settingsApplyService.SaveAndApplyImage(settings);
    }

    private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        settings.DefaultProfile.BrightnessPercent = value;
        BrightnessValueText.Text = value.ToString();
        SaveImageSettings();
    }

    private void SaturationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        settings.DefaultProfile.SaturationBoost = percent / 100.0;
        SaturationValueText.Text = percent.ToString();
        SaveImageSettings();
    }

    private void KelvinSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        settings.DefaultProfile.ColorTemperatureKelvin = value;
        KelvinValueText.Text = value.ToString();
        SaveImageSettings();
    }

    private void BlackCutoffSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        settings.DefaultProfile.BlackCutoffThreshold = value;
        BlackCutoffValueText.Text = value.ToString();
        SaveImageSettings();
    }

    private void GammaSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        double value = Math.Round(e.NewValue / 10.0, 1);
        settings.DefaultProfile.GammaValue = value;
        GammaValueText.Text = value.ToString("0.0");
        SaveImageSettings();
    }

    private void AttackSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        double value = Math.Round(e.NewValue, 1);
        AttackSpeedValueText.Text = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        settings.MotionAttackSpeed = value;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void DecaySpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        double value = Math.Round(e.NewValue, 1);
        DecaySpeedValueText.Text = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        settings.MotionDecaySpeed = value;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void ColorSensitivitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        double value = Math.Round(e.NewValue, 1);
        ColorSensitivityValueText.Text = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        settings.ColorSensitivity = value;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void MinBrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        byte value = (byte)Math.Round(e.NewValue);
        MinBrightnessValueText.Text = value.ToString();
        settings.MinimumBrightnessFloor = value;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void ZonePeakWeightSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        ZonePeakWeightValueText.Text = percent.ToString();
        settings.ZonePeakWeight = percent / 100.0;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void ShadowBoostSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        double value = Math.Round(e.NewValue / 10.0, 1);
        ShadowBoostValueText.Text = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        settings.ShadowBoostStrength = value;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void NoiseFloorSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        byte value = (byte)Math.Round(e.NewValue);
        NoiseFloorValueText.Text = value.ToString();
        settings.NoiseFloor = value;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void EdgeFeatherSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        settings.EdgeFeatherPixels = value;
        EdgeFeatherValueText.Text = value.ToString();
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    // NOWOŚĆ: slider "Wygładzanie fazowe" - steruje PhaseSmoothingStrength (tłumienie
    // aliasingu fazowego siatki próbkowania przy bardzo powolnym, subpikselowym ruchu treści).
    private void PhaseSmoothingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        PhaseSmoothingValueText.Text = percent.ToString();
        settings.PhaseSmoothingStrength = percent / 100.0;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    // NOWOŚĆ: trzy slidery kalibracji per-kanał RGB - korekta rozjazdu koloru ekran <-> LED.
    private void ChannelGainRSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        ChannelGainRValueText.Text = percent.ToString();
        settings.ChannelGainR = percent / 100.0;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void ChannelGainGSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        ChannelGainGValueText.Text = percent.ToString();
        settings.ChannelGainG = percent / 100.0;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void ChannelGainBSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        ChannelGainBValueText.Text = percent.ToString();
        settings.ChannelGainB = percent / 100.0;
        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void ApplyLiveDynamics()
    {
        // SaveImageSettings() woła settingsApplyService.SaveAndApplyImage(), które zapisuje
        // ustawienia na dysk ORAZ wywołuje engineHost.ApplyLiveSettings() - dzięki temu
        // wszystkie parametry z tej strony trafiają do żywego ImageProcessor natychmiast,
        // bez restartu Video Sync.
        SaveImageSettings();
    }

    private void BlackBarToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        bool enabled = BlackBarToggle.IsOn;
        settings.EnableBlackBarDetection = enabled;

        BlackBarPanel.Opacity = enabled ? 1.0 : 0.45;
        BlackBarPanel.IsHitTestVisible = enabled;

        SaveImageSettings();
        ApplyLiveBlackBarSettings();
    }

    private void BlackBarThresholdSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        byte value = (byte)Math.Round(e.NewValue);
        BlackBarThresholdValueText.Text = value.ToString();
        settings.BlackBarThreshold = value;
        SaveImageSettings();
        ApplyLiveBlackBarSettings();
    }

    private void BlackBarMinRatioSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        BlackBarMinRatioValueText.Text = percent.ToString();
        settings.BlackBarMinRatio = percent / 100.0;
        SaveImageSettings();
        ApplyLiveBlackBarSettings();
    }

    private void ApplyLiveBlackBarSettings()
    {
        if (mainWindow?.EngineHost is null || settings is null)
        {
            return;
        }

        mainWindow.EngineHost.SetBlackBarDetectionEnabled(settings.EnableBlackBarDetection);
        mainWindow.EngineHost.SetBlackBarDetectionParameters(settings.BlackBarThreshold, settings.BlackBarMinRatio);
    }

    private void LedCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (isInitializing || settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        settings.LedCount = (int)args.NewValue;
        SaveCaptureSettings();
    }

    private void SamplingDepthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        settings.SamplingDepth = value;
        SamplingDepthValueText.Text = value.ToString();
        SaveCaptureSettings();
    }

    private void WallColorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (isInitializing || settings is null)
        {
            return;
        }

        bool enabled = WallColorToggle.IsOn;
        SetWallPanelEnabled(enabled);

        if (!enabled)
        {
            settings.WallColorHex = null;
            settings.WallColorStrength = 0f;
            SaveImageSettings();
            return;
        }

        ApplyWallColorSettings();
    }

    private async void WallColorPickerButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            Color = wallColor,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsMoreButtonVisible = true
        };

        var dialog = new ContentDialog
        {
            Title = "Wybierz kolor ściany",
            PrimaryButtonText = "OK",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = picker
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        wallColor = picker.Color;
        UpdateWallColorPreview(wallColor);

        if (WallColorToggle.IsOn)
        {
            ApplyWallColorSettings();
        }
    }

    private async void SaveAsProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (settings is null || settingsApplyService is null)
        {
            return;
        }

        var nameTextBox = new TextBox
        {
            PlaceholderText = "np. Kino wieczorem"
        };

        var dialog = new ContentDialog
        {
            Title = "Zapisz jako profil",
            PrimaryButtonText = "Zapisz",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = nameTextBox
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        string profileName = nameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        var newProfile = new AmbilightEngine.Core.Models.AppProfile
        {
            DisplayName = profileName,
            BrightnessPercent = settings.DefaultProfile.BrightnessPercent,
            SaturationBoost = settings.DefaultProfile.SaturationBoost,
            ColorTemperatureKelvin = settings.DefaultProfile.ColorTemperatureKelvin,
            BlackCutoffThreshold = settings.DefaultProfile.BlackCutoffThreshold,
            GammaValue = settings.DefaultProfile.GammaValue
        };

        settings.Profiles.Add(newProfile);
        SaveImageSettings();
    }

    private void ResetImageDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (settings is null)
        {
            return;
        }

        isInitializing = true;

        // Domyślne wartości – możesz je dostosować, jeśli chcesz inne „fabryczne” ustawienia
        settings.DefaultProfile.BrightnessPercent = 100;
        settings.DefaultProfile.SaturationBoost = 1.0;
        settings.DefaultProfile.ColorTemperatureKelvin = 6500;
        settings.DefaultProfile.BlackCutoffThreshold = 8;
        settings.DefaultProfile.GammaValue = 2.2;

        settings.MotionAttackSpeed = 1.0;
        settings.MotionDecaySpeed = 1.0;
        settings.ColorSensitivity = 1.0;
        settings.MinimumBrightnessFloor = 5;

        settings.ZonePeakWeight = 0.3;
        settings.ShadowBoostStrength = 1.0;
        settings.NoiseFloor = 4;
        settings.EdgeFeatherPixels = 2;

        // NOWOŚĆ: przywracamy też domyślne wartości wygładzania fazowego i kalibracji RGB.
        settings.PhaseSmoothingStrength = 0.0;
        settings.ChannelGainR = 1.0;
        settings.ChannelGainG = 1.0;
        settings.ChannelGainB = 1.0;

        // Uaktualnij suwaki i wartości tekstowe
        BrightnessSlider.Value = settings.DefaultProfile.BrightnessPercent;
        BrightnessValueText.Text = settings.DefaultProfile.BrightnessPercent.ToString();

        SaturationSlider.Value = settings.DefaultProfile.SaturationBoost * 100.0;
        SaturationValueText.Text = ((int)Math.Round(settings.DefaultProfile.SaturationBoost * 100.0)).ToString();

        KelvinSlider.Value = settings.DefaultProfile.ColorTemperatureKelvin;
        KelvinValueText.Text = settings.DefaultProfile.ColorTemperatureKelvin.ToString();

        BlackCutoffSlider.Value = settings.DefaultProfile.BlackCutoffThreshold;
        BlackCutoffValueText.Text = settings.DefaultProfile.BlackCutoffThreshold.ToString();

        GammaSlider.Value = settings.DefaultProfile.GammaValue * 10.0;
        GammaValueText.Text = settings.DefaultProfile.GammaValue.ToString("0.0");

        AttackSpeedSlider.Value = settings.MotionAttackSpeed;
        AttackSpeedValueText.Text = settings.MotionAttackSpeed.ToString("0.0");

        DecaySpeedSlider.Value = settings.MotionDecaySpeed;
        DecaySpeedValueText.Text = settings.MotionDecaySpeed.ToString("0.0");

        ColorSensitivitySlider.Value = settings.ColorSensitivity;
        ColorSensitivityValueText.Text = settings.ColorSensitivity.ToString("0.0");

        MinBrightnessSlider.Value = settings.MinimumBrightnessFloor;
        MinBrightnessValueText.Text = settings.MinimumBrightnessFloor.ToString();

        int peakWeightPercent = (int)Math.Round(settings.ZonePeakWeight * 100.0);
        ZonePeakWeightSlider.Value = peakWeightPercent;
        ZonePeakWeightValueText.Text = peakWeightPercent.ToString();

        ShadowBoostSlider.Value = settings.ShadowBoostStrength * 10.0;
        ShadowBoostValueText.Text = settings.ShadowBoostStrength.ToString("0.0");

        NoiseFloorSlider.Value = settings.NoiseFloor;
        NoiseFloorValueText.Text = settings.NoiseFloor.ToString();

        EdgeFeatherSlider.Value = settings.EdgeFeatherPixels;
        EdgeFeatherValueText.Text = settings.EdgeFeatherPixels.ToString();

        int phaseSmoothingPercent = (int)Math.Round(settings.PhaseSmoothingStrength * 100.0);
        PhaseSmoothingSlider.Value = phaseSmoothingPercent;
        PhaseSmoothingValueText.Text = phaseSmoothingPercent.ToString();

        int channelGainRPercent = (int)Math.Round(settings.ChannelGainR * 100.0);
        ChannelGainRSlider.Value = channelGainRPercent;
        ChannelGainRValueText.Text = channelGainRPercent.ToString();

        int channelGainGPercent = (int)Math.Round(settings.ChannelGainG * 100.0);
        ChannelGainGSlider.Value = channelGainGPercent;
        ChannelGainGValueText.Text = channelGainGPercent.ToString();

        int channelGainBPercent = (int)Math.Round(settings.ChannelGainB * 100.0);
        ChannelGainBSlider.Value = channelGainBPercent;
        ChannelGainBValueText.Text = channelGainBPercent.ToString();

        isInitializing = false;

        SaveImageSettings();
        ApplyLiveDynamics();
    }

    private void WallStrengthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WallStrengthValueText is not null)
        {
            WallStrengthValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
        }

        if (isInitializing || settings is null || !WallColorToggle.IsOn)
        {
            return;
        }

        ApplyWallColorSettings();
    }

    private void ApplyWallColorSettings()
    {
        if (settings is null)
        {
            return;
        }

        settings.WallColorHex = ColorToHex(wallColor);
        settings.WallColorStrength = (float)(WallStrengthSlider.Value / 100.0);
        SaveImageSettings();
    }

    private void UpdateWallColorPreview(Color color)
    {
        WallColorPreview.Background = new SolidColorBrush(color);
        WallColorHexText.Text = ColorToHex(color);
    }

    private void SetWallPanelEnabled(bool enabled)
    {
        WallColorPanel.Opacity = enabled ? 1.0 : 0.45;
        WallColorPanel.IsHitTestVisible = enabled;
    }

    private static string ColorToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static Color HexToColor(string hex)
    {
        string value = hex.Trim().TrimStart('#');

        if (value.Length == 6)
        {
            byte r = Convert.ToByte(value.Substring(0, 2), 16);
            byte g = Convert.ToByte(value.Substring(2, 2), 16);
            byte b = Convert.ToByte(value.Substring(4, 2), 16);
            return Color.FromArgb(255, r, g, b);
        }

        return Color.FromArgb(255, 128, 128, 128);
 
    }

}