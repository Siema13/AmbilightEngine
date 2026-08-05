using System;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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