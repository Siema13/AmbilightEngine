using System;
using AmbilightEngine.Core.SystemState;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using AmbilightEngine.Services;

namespace AmbilightEngine.Pages;

public sealed partial class SettingsImagePage : Page
{
    private MainWindow? mainWindow;
    private AmbilightSettings? settings;
    private bool isInitializing;

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

        isInitializing = false;
    }

    private void SaveCaptureSettings()
    {
        if (settings is null || settingsApplyService is null)
        {
            return;
        }

        settingsApplyService.SaveAndApplyGeometry(settings);
    }
    private void SaveSettings()
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
        SaveSettings();
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
        SaveSettings();
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
        SaveSettings();
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
        SaveSettings();
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
        SaveSettings();
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
    private ISettingsApplyService? settingsApplyService;
}