using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AmbilightEngine.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AmbilightEngine.Pages
{
    public sealed partial class ProfilesPage : Page
    {
        private MainWindow? mainWindow;
        private ObservableCollection<AppProfile> profiles = new();
        private bool isLivePreviewEnabled;

        public ProfilesPage()
        {
            InitializeComponent();
            Loaded += ProfilesPage_Loaded;
            Unloaded += ProfilesPage_Unloaded;
        }

        private void ProfilesPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;
            if (mainWindow == null)
            {
                ShowInfo(
                    InfoBarSeverity.Error,
                    "Brak głównego okna",
                    "Nie znaleziono głównego okna aplikacji. Profile nie mogą być wczytane.");

                SaveStatusText.Text = "Nie znaleziono głównego okna aplikacji.";
                profiles = new ObservableCollection<AppProfile>();
                ProfilesList.ItemsSource = profiles;
                UpdateEmptyState();
                return;
            }

            EnsureDefaultProfile();

            profiles = new ObservableCollection<AppProfile>(
                mainWindow.Settings.Profiles ?? new List<AppProfile>());

            ProfilesList.ItemsSource = profiles;
            DataContext = mainWindow.Settings;

            if (DefaultStaticColorPicker != null)
            {
                AppProfile defaultProfile = mainWindow.Settings.DefaultProfile;
                DefaultStaticColorPicker.Color = Windows.UI.Color.FromArgb(
                    255,
                    defaultProfile.StaticColorR,
                    defaultProfile.StaticColorG,
                    defaultProfile.StaticColorB);
            }

            SaveStatusText.Text = string.Empty;

            if (profiles.Count == 0)
            {
                ShowInfo(
                    InfoBarSeverity.Informational,
                    "Brak profili",
                    "Nie masz jeszcze żadnych profili. Dodaj pierwszy, aby przypisać ustawienia do aplikacji.");
            }
            else
            {
                ShowInfo(
                    InfoBarSeverity.Informational,
                    "Profile wczytane",
                    $"Załadowano {profiles.Count} profili aplikacji oraz profil domyślny.");
            }

            UpdateEmptyState();
        }

        private void EnsureDefaultProfile()
        {
            if (mainWindow == null)
            {
                return;
            }

            mainWindow.Settings.DefaultProfile ??= new AppProfile();

            AppProfile defaultProfile = mainWindow.Settings.DefaultProfile;

            if (string.IsNullOrWhiteSpace(defaultProfile.DisplayName))
            {
                defaultProfile.DisplayName = "Domyślny";
            }

            defaultProfile.ExecutableFileName = string.Empty;
            defaultProfile.AllowBackgroundActivation = false;
            defaultProfile.Priority = 0;
            defaultProfile.IsBuiltInDefault = true;
        }

        private void UpdateEmptyState()
        {
            if (ProfilesEmptyStatePanel == null || ProfilesList == null)
            {
                return;
            }

            bool hasProfiles = profiles.Count > 0;
            ProfilesEmptyStatePanel.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
            ProfilesList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddPresetProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem || menuItem.Tag is not string tagValue)
            {
                return;
            }

            if (!Enum.TryParse(tagValue, out ProfilePresetKind presetKind))
            {
                presetKind = ProfilePresetKind.Custom;
            }

            string displayName = ProfilePresetCatalog.GetDefaultDisplayName(presetKind);
            AppProfile newProfile = ProfilePresetCatalog.CreateFromPreset(presetKind, displayName);
            profiles.Add(newProfile);

            SaveStatusText.Text =
                $"Dodano profil na podstawie szablonu „{displayName}”. Zapisz zmiany, aby utrwalić konfigurację.";

            ShowInfo(
                InfoBarSeverity.Informational,
                "Dodano profil",
                $"Dodano profil „{displayName}” na podstawie szablonu. Pamiętaj o zapisaniu zmian.");

            UpdateEmptyState();
        }

        private void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not AppProfile profileToRemove)
            {
                return;
            }

            profiles.Remove(profileToRemove);

            SaveStatusText.Text = "Usunięto profil. Zapisz zmiany, aby zaktualizować konfigurację.";
            ShowInfo(
                InfoBarSeverity.Warning,
                "Profil usunięty",
                "Profil został usunięty. Zapisz zmiany, aby zapisać nowy stan.");

            UpdateEmptyState();
        }

        private async void PickApplicationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not AppProfile profile)
            {
                return;
            }

            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");

                if (mainWindow != null)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(
                        picker,
                        WinRT.Interop.WindowNative.GetWindowHandle(mainWindow));
                }

                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                profile.ExecutableFileName = file.Name;

                SaveStatusText.Text = "Wybrano aplikację .exe. Zapisz zmiany, aby powiązać profil.";
                ShowInfo(
                    InfoBarSeverity.Informational,
                    "Aplikacja wybrana",
                    $"Wybrano plik: {file.Name}. Zapisz zmiany, aby utrwalić powiązanie.");
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Błąd wyboru aplikacji: {ex.Message}";
                ShowInfo(
                    InfoBarSeverity.Error,
                    "Błąd wyboru aplikacji",
                    $"Wystąpił błąd podczas wyboru pliku .exe: {ex.Message}");
            }
        }

        private void ResetProfileImageDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not AppProfile profile)
            {
                return;
            }

            ResetImageParameters(profile);

            SaveStatusText.Text =
                "Przywrócono domyślne parametry obrazu dla profilu. Zapisz zmiany, aby je utrwalić.";

            ShowInfo(
                InfoBarSeverity.Informational,
                "Domyślne parametry",
                "Parametry obrazu dla tego profilu zostały zresetowane do wartości domyślnych.");
        }

        private void ResetDefaultProfileImageDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            ResetImageParameters(profile);

            SaveStatusText.Text =
                "Przywrócono neutralne parametry profilu domyślnego. Zapisz zmiany, aby je utrwalić.";

            ShowInfo(
                InfoBarSeverity.Informational,
                "Profil domyślny",
                "Przywrócono neutralne parametry obrazu profilu domyślnego.");
        }

        private static void ResetImageParameters(AppProfile profile)
        {
            profile.BrightnessPercent = 100;
            profile.SaturationBoost = 1.0;
            profile.SmoothingSpeedMs = 120;
            profile.BlackCutoffThreshold = 8;
            profile.ColorTemperatureKelvin = 6500;
            profile.GammaValue = 2.2;
        }

        private void SaveChangesButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null)
            {
                SaveStatusText.Text = "Brak dostępu do ustawień aplikacji.";
                ShowInfo(
                    InfoBarSeverity.Error,
                    "Brak ustawień",
                    "Brak dostępu do ustawień aplikacji. Zamknij i uruchom ponownie AmbilightEngine.");

                return;
            }

            try
            {
                EnsureDefaultProfile();
                mainWindow.Settings.Profiles = new List<AppProfile>(profiles);
                mainWindow.SettingsService.Save(mainWindow.Settings);
                mainWindow.EngineHost.RefreshProfileList();

                SaveStatusText.Text = "Zapisano profile aplikacji oraz profil domyślny.";
                ShowInfo(
                    InfoBarSeverity.Success,
                    "Zapisano profile",
                    "Zmiany w profilach aplikacji oraz profilu domyślnym zostały zapisane i przekazane do silnika.");
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Błąd zapisu: {ex.Message}";
                ShowInfo(
                    InfoBarSeverity.Error,
                    "Błąd zapisu",
                    $"Nie udało się zapisać profili: {ex.Message}");
            }
        }

        private void EnableLivePreviewButton_Click(object sender, RoutedEventArgs e)
        {
            isLivePreviewEnabled = true;
            SaveStatusText.Text =
                "Podgląd na żywo jest włączony. Zmień dowolny suwak parametrów obrazu, aby zobaczyć efekt na LED-ach.";
        }

        private void DisableLivePreviewButton_Click(object sender, RoutedEventArgs e)
        {
            isLivePreviewEnabled = false;
            mainWindow?.EngineHost.EndProfilePreview();

            SaveStatusText.Text =
                "Podgląd na żywo został wyłączony. Automatyczne profile znów działają normalnie.";
        }

        private void ProfileImageSlider_ValueChanged(
            object sender,
            RangeBaseValueChangedEventArgs e)
        {
            if (!isLivePreviewEnabled ||
                mainWindow == null ||
                sender is not FrameworkElement element ||
                element.DataContext is not AppProfile profile)
            {
                return;
            }

            mainWindow.EngineHost.PreviewProfile(profile);
        }

        private void DefaultProfileImageSlider_ValueChanged(
            object sender,
            RangeBaseValueChangedEventArgs e)
        {
            if (!isLivePreviewEnabled || mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            mainWindow.EngineHost.PreviewProfile(profile);
        }

        private void ActionTypeImageDsp_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Tag is AppProfile profile)
            {
                profile.ActionType = ProfileActionType.ImageDsp;
                SaveStatusText.Text =
                    "Zmieniono typ akcji profilu na parametry obrazu. Zapisz zmiany, aby utrwalić konfigurację.";
            }
        }

        private void ActionTypeStaticColor_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Tag is AppProfile profile)
            {
                profile.ActionType = ProfileActionType.StaticColor;
                SaveStatusText.Text =
                    "Zmieniono typ akcji profilu na stały kolor LED. Zapisz zmiany, aby utrwalić konfigurację.";
            }
        }

        private void ActionTypeWledEffect_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Tag is AppProfile profile)
            {
                profile.ActionType = ProfileActionType.WledEffect;
                SaveStatusText.Text =
                    "Zmieniono typ akcji profilu na efekt WLED. Zapisz zmiany, aby utrwalić konfigurację.";
            }
        }

        private void DefaultActionTypeImageDsp_Checked(object sender, RoutedEventArgs e)
        {
            if (mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            profile.ActionType = ProfileActionType.ImageDsp;
            SaveStatusText.Text =
                "Profil domyślny będzie przywracał parametry obrazu Video Sync. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void DefaultActionTypeStaticColor_Checked(object sender, RoutedEventArgs e)
        {
            if (mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            profile.ActionType = ProfileActionType.StaticColor;
            SaveStatusText.Text =
                "Profil domyślny będzie przywracał stały kolor LED. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void DefaultActionTypeWledEffect_Checked(object sender, RoutedEventArgs e)
        {
            if (mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            profile.ActionType = ProfileActionType.WledEffect;
            SaveStatusText.Text =
                "Profil domyślny będzie przywracał efekt WLED. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void ProfileStaticColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (sender.Tag is not AppProfile profile)
            {
                return;
            }

            profile.StaticColorR = args.NewColor.R;
            profile.StaticColorG = args.NewColor.G;
            profile.StaticColorB = args.NewColor.B;

            SaveStatusText.Text = "Zmieniono kolor stały profilu. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void DefaultStaticColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            profile.StaticColorR = args.NewColor.R;
            profile.StaticColorG = args.NewColor.G;
            profile.StaticColorB = args.NewColor.B;

            SaveStatusText.Text =
                "Zmieniono stały kolor profilu domyślnego. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void ProfilesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            isLivePreviewEnabled = false;
            mainWindow?.EngineHost.EndProfilePreview();
        }

        private void ShowInfo(InfoBarSeverity severity, string title, string message)
        {
            if (ProfilesInfoBar == null)
            {
                return;
            }

            ProfilesInfoBar.Severity = severity;
            ProfilesInfoBar.Title = title;
            ProfilesInfoBar.Message = message;
            ProfilesInfoBar.IsOpen = true;
        }
    }
}
