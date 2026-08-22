using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AmbilightEngine.Pages
{
    public sealed partial class SettingsGeneralPage : Page
    {
        private enum ThemeColorSlot
        {
            Accent,
            WindowBackground,
            ContentBackground,
            CardSurface,
            BackgroundAccent
        }

        private MainWindow? mainWindow;
        private bool isInitializing;
        private ThemeColorSlot currentColorSlot;

        public SettingsGeneralPage()
        {
            InitializeComponent();
            Loaded += SettingsGeneralPage_Loaded;
        }

        private void SettingsGeneralPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;
            if (mainWindow is null)
            {
                return;
            }

            isInitializing = true;

            UseCustomThemeSwitch.IsOn = mainWindow.Settings.UseCustomTheme;

            GlassOpacitySlider.Value = mainWindow.Settings.UiGlassOpacity * 100.0;
            GlassOpacityValueText.Text = $"{(int)Math.Round(mainWindow.Settings.UiGlassOpacity * 100.0)}%";

            CustomThemePanel.Visibility = mainWindow.Settings.UseCustomTheme
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateThemePresetButtons();
            UpdateBackgroundStyleButtons();
            UpdateColorPreviews();
            OsdEnabledSwitch.IsOn = mainWindow.Settings.OsdEnabled;
            isInitializing = false;
        }

        private void ThemePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null || sender is not Button button || button.Tag is not string themeName)
            {
                return;
            }

            bool isDarkMode = Application.Current.RequestedTheme == ApplicationTheme.Dark;

            mainWindow.Settings.UseCustomTheme = false;
            mainWindow.Settings.AccentThemeName = themeName;

            mainWindow.SettingsService.Save(mainWindow.Settings);

            UseCustomThemeSwitch.IsOn = false;
            CustomThemePanel.Visibility = Visibility.Collapsed;

            mainWindow.ThemeService.ApplyTheme(themeName, isDarkMode);
            mainWindow.UpdateBackdropForCustomTheme(false);

            UpdateThemePresetButtons();
        }

        private void UseCustomThemeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null || isInitializing)
            {
                return;
            }

            mainWindow.Settings.UseCustomTheme = UseCustomThemeSwitch.IsOn;
            CustomThemePanel.Visibility = mainWindow.Settings.UseCustomTheme
                ? Visibility.Visible
                : Visibility.Collapsed;

            mainWindow.SettingsService.Save(mainWindow.Settings);
            mainWindow.UpdateBackdropForCustomTheme(mainWindow.Settings.UseCustomTheme);

            if (mainWindow.Settings.UseCustomTheme)
            {
                ApplyCustomThemeLive();
            }
            else
            {
                bool isDarkMode = Application.Current.RequestedTheme == ApplicationTheme.Dark;
                mainWindow.ThemeService.ApplyTheme(mainWindow.Settings.AccentThemeName, isDarkMode);
            }

            UpdateThemePresetButtons();
        }

        private void OsdEnabledSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null || isInitializing)
            {
                return;
            }

            mainWindow.Settings.OsdEnabled = OsdEnabledSwitch.IsOn;
            mainWindow.SettingsService.Save(mainWindow.Settings);
        }
        private DispatcherTimer? glassOpacityDebounceTimer;

        private void GlassOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (mainWindow is null || isInitializing)
            {
                return;
            }

            double opacity = Math.Round(e.NewValue / 100.0, 2);
            mainWindow.Settings.UiGlassOpacity = opacity;
            GlassOpacityValueText.Text = $"{(int)Math.Round(e.NewValue)}%";

            glassOpacityDebounceTimer?.Stop();
            glassOpacityDebounceTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            glassOpacityDebounceTimer.Tick -= GlassOpacityDebounceTimer_Tick;
            glassOpacityDebounceTimer.Tick += GlassOpacityDebounceTimer_Tick;
            glassOpacityDebounceTimer.Start();
        }

        private void GlassOpacityDebounceTimer_Tick(object? sender, object e)
        {
            glassOpacityDebounceTimer?.Stop();

            if (mainWindow is null)
            {
                return;
            }

            mainWindow.SettingsService.Save(mainWindow.Settings);
            ApplyCustomThemeLive();
        }

        private void BackgroundStyleButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null || isInitializing || sender is not Button button || button.Tag is not string styleName)
            {
                return;
            }

            mainWindow.Settings.CustomBackgroundStyle = styleName;
            mainWindow.SettingsService.Save(mainWindow.Settings);
            mainWindow.UpdateBackdropForCustomTheme(mainWindow.Settings.UseCustomTheme);

            UpdateBackgroundStyleButtons();
            ApplyCustomThemeLive();
        }

        // ===== KOLORY MOTYWU (kafelki + dialog) =====

        private async void AccentColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null)
            {
                return;
            }

            currentColorSlot = ThemeColorSlot.Accent;
            // w AccentColorButton_Click
            ColorDialogDescription.Text = "Kolor akcentu interfejsu - przyciski i elementy aktywne we wszystkich kolumnach.";
            DialogColorPicker.Color = Color.FromArgb(
                255,
                mainWindow.Settings.CustomAccentR,
                mainWindow.Settings.CustomAccentG,
                mainWindow.Settings.CustomAccentB);

            await ColorPickerDialog.ShowAsync();
            ApplySelectedColorFromDialog();
        }

        private async void WindowBackgroundColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null)
            {
                return;
            }

            currentColorSlot = ThemeColorSlot.WindowBackground;
            // w WindowBackgroundColorButton_Click
            ColorDialogDescription.Text = "Bazowy kolor gradientu tła aplikacji (widoczny za interfejsem).";
            DialogColorPicker.Color = Color.FromArgb(
                255,
                mainWindow.Settings.CustomWindowBackgroundR,
                mainWindow.Settings.CustomWindowBackgroundG,
                mainWindow.Settings.CustomWindowBackgroundB);

            await ColorPickerDialog.ShowAsync();
            ApplySelectedColorFromDialog();
        }

        private async void ContentBackgroundColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null)
            {
                return;
            }

            currentColorSlot = ThemeColorSlot.ContentBackground;
            // w ContentBackgroundColorButton_Click
            ColorDialogDescription.Text = "Kolor tła paneli nawigacji (kolumna 1 i kolumna 2 z listą sekcji).";
            DialogColorPicker.Color = Color.FromArgb(
                255,
                mainWindow.Settings.CustomContentBackgroundR,
                mainWindow.Settings.CustomContentBackgroundG,
                mainWindow.Settings.CustomContentBackgroundB);

            await ColorPickerDialog.ShowAsync();
            ApplySelectedColorFromDialog();
        }

        private async void CardSurfaceColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null)
            {
                return;
            }

            currentColorSlot = ThemeColorSlot.CardSurface;
            // w CardSurfaceColorButton_Click
            ColorDialogDescription.Text = "Kolor tła kart i paneli w trzeciej kolumnie (suwaki, przełączniki, color pickery).";
            DialogColorPicker.Color = Color.FromArgb(
                255,
                mainWindow.Settings.CustomCardSurfaceR,
                mainWindow.Settings.CustomCardSurfaceG,
                mainWindow.Settings.CustomCardSurfaceB);

            await ColorPickerDialog.ShowAsync();
            ApplySelectedColorFromDialog();
        }

        private async void BackgroundAccentColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow is null)
            {
                return;
            }

            currentColorSlot = ThemeColorSlot.BackgroundAccent;
            ColorDialogDescription.Text = "Kolor akcentu dla stylów tła Aurora, Warm Dusk, Velvet Glow i Studio.";
            DialogColorPicker.Color = Color.FromArgb(
                255,
                mainWindow.Settings.CustomBackgroundAccentR,
                mainWindow.Settings.CustomBackgroundAccentG,
                mainWindow.Settings.CustomBackgroundAccentB);

            await ColorPickerDialog.ShowAsync();
            ApplySelectedColorFromDialog();
        }

        private void ApplySelectedColorFromDialog()
        {
            if (mainWindow is null || !mainWindow.Settings.UseCustomTheme)
            {
                return;
            }

            Color selected = DialogColorPicker.Color;

            switch (currentColorSlot)
            {
                case ThemeColorSlot.Accent:
                    mainWindow.Settings.CustomAccentR = selected.R;
                    mainWindow.Settings.CustomAccentG = selected.G;
                    mainWindow.Settings.CustomAccentB = selected.B;
                    break;

                case ThemeColorSlot.WindowBackground:
                    mainWindow.Settings.CustomWindowBackgroundR = selected.R;
                    mainWindow.Settings.CustomWindowBackgroundG = selected.G;
                    mainWindow.Settings.CustomWindowBackgroundB = selected.B;
                    mainWindow.UpdateBackdropForCustomTheme(true);
                    break;

                case ThemeColorSlot.ContentBackground:
                    mainWindow.Settings.CustomContentBackgroundR = selected.R;
                    mainWindow.Settings.CustomContentBackgroundG = selected.G;
                    mainWindow.Settings.CustomContentBackgroundB = selected.B;
                    break;

                case ThemeColorSlot.CardSurface:
                    mainWindow.Settings.CustomCardSurfaceR = selected.R;
                    mainWindow.Settings.CustomCardSurfaceG = selected.G;
                    mainWindow.Settings.CustomCardSurfaceB = selected.B;
                    break;

                case ThemeColorSlot.BackgroundAccent:
                    mainWindow.Settings.CustomBackgroundAccentR = selected.R;
                    mainWindow.Settings.CustomBackgroundAccentG = selected.G;
                    mainWindow.Settings.CustomBackgroundAccentB = selected.B;
                    mainWindow.UpdateBackdropForCustomTheme(true);
                    break;
            }

            mainWindow.SettingsService.Save(mainWindow.Settings);
            UpdateColorPreviews();
            ApplyCustomThemeLive();
        }

        private void UpdateColorPreviews()
        {
            if (mainWindow is null)
            {
                return;
            }

            AccentPreview.Background = new SolidColorBrush(Color.FromArgb(
                255,
                mainWindow.Settings.CustomAccentR,
                mainWindow.Settings.CustomAccentG,
                mainWindow.Settings.CustomAccentB));

            WindowBackgroundPreview.Background = new SolidColorBrush(Color.FromArgb(
                255,
                mainWindow.Settings.CustomWindowBackgroundR,
                mainWindow.Settings.CustomWindowBackgroundG,
                mainWindow.Settings.CustomWindowBackgroundB));

            ContentBackgroundPreview.Background = new SolidColorBrush(Color.FromArgb(
                255,
                mainWindow.Settings.CustomContentBackgroundR,
                mainWindow.Settings.CustomContentBackgroundG,
                mainWindow.Settings.CustomContentBackgroundB));

            CardSurfacePreview.Background = new SolidColorBrush(Color.FromArgb(
                255,
                mainWindow.Settings.CustomCardSurfaceR,
                mainWindow.Settings.CustomCardSurfaceG,
                mainWindow.Settings.CustomCardSurfaceB));

            BackgroundAccentPreview.Background = new SolidColorBrush(Color.FromArgb(
                255,
                mainWindow.Settings.CustomBackgroundAccentR,
                mainWindow.Settings.CustomBackgroundAccentG,
                mainWindow.Settings.CustomBackgroundAccentB));
        }

        // ===== PRESET / STYL TŁA — WIZUALNE STANY PRZYCISKÓW =====

        private void UpdateThemePresetButtons()
        {
            if (mainWindow is null)
            {
                return;
            }

            SetThemeButtonState(ThemeBlueButton, "Blue");
            SetThemeButtonState(ThemeIndigoButton, "Indigo");
            SetThemeButtonState(ThemeTealButton, "Teal");
            SetThemeButtonState(ThemeGreenButton, "Green");
            SetThemeButtonState(ThemeOrangeButton, "Orange");
            SetThemeButtonState(ThemePurpleButton, "Purple");
        }

        private void SetThemeButtonState(Button button, string themeName)
        {
            bool isActive = !mainWindow!.Settings.UseCustomTheme &&
                            string.Equals(
                                mainWindow.Settings.AccentThemeName,
                                themeName,
                                StringComparison.OrdinalIgnoreCase);

            button.Background = new SolidColorBrush(isActive
                ? Color.FromArgb(255, 42, 38, 56)
                : Color.FromArgb(30, 255, 255, 255));

            button.BorderBrush = new SolidColorBrush(isActive
                ? Color.FromArgb(255, 124, 189, 255)
                : Color.FromArgb(70, 255, 255, 255));

            button.BorderThickness = new Thickness(isActive ? 2 : 1);
        }

        private void UpdateBackgroundStyleButtons()
        {
            if (mainWindow is null)
            {
                return;
            }

            SetStyleButtonState(PureDarkStyleButton, "PureDark");
            SetStyleButtonState(SoftGradientStyleButton, "SoftGradient");
            SetStyleButtonState(AmbientHaloStyleButton, "AmbientHalo");
            SetStyleButtonState(GraphiteStyleButton, "Graphite");
            SetStyleButtonState(DeepSpaceStyleButton, "DeepSpace");
            SetStyleButtonState(WarmDuskStyleButton, "WarmDusk");
            SetStyleButtonState(AuroraStyleButton, "Aurora");
            SetStyleButtonState(StudioStyleButton, "Studio");
            SetStyleButtonState(ContrastLayeredStyleButton, "ContrastLayered");
            SetStyleButtonState(VelvetGlowStyleButton, "VelvetGlow");
        }

        private void SetStyleButtonState(Button button, string styleName)
        {
            bool isActive = string.Equals(
                mainWindow!.Settings.CustomBackgroundStyle,
                styleName,
                StringComparison.OrdinalIgnoreCase);

            button.Background = new SolidColorBrush(isActive
                ? Color.FromArgb(255, 42, 38, 56)
                : Color.FromArgb(30, 255, 255, 255));

            button.BorderBrush = new SolidColorBrush(isActive
                ? Color.FromArgb(255, 196, 185, 255)
                : Color.FromArgb(70, 255, 255, 255));

            button.BorderThickness = new Thickness(isActive ? 2 : 1);
        }

        private void ApplyCustomThemeLive()
        {
            if (mainWindow is null || !mainWindow.Settings.UseCustomTheme)
            {
                return;
            }

            bool isDarkMode = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            mainWindow.ThemeService.ApplyCustomTheme(mainWindow.Settings, isDarkMode);
            UpdateThemePresetButtons();
        }
    }
}