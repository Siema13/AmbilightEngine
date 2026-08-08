using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AmbilightEngine.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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
                    severity: InfoBarSeverity.Error,
                    title: "Brak głównego okna",
                    message: "Nie znaleziono głównego okna aplikacji. Profile nie mogą być wczytane.");

                SaveStatusText.Text = "Nie znaleziono głównego okna aplikacji.";
                profiles = new ObservableCollection<AppProfile>();
                ProfilesList.ItemsSource = profiles;
                UpdateEmptyState();
                return;
            }

            profiles = new ObservableCollection<AppProfile>(
                mainWindow.Settings.Profiles ?? new List<AppProfile>());

            ProfilesList.ItemsSource = profiles;
            SaveStatusText.Text = string.Empty;

            if (profiles.Count == 0)
            {
                ShowInfo(
                    severity: InfoBarSeverity.Informational,
                    title: "Brak profili",
                    message: "Nie masz jeszcze żadnych profili. Dodaj pierwszy, aby przypisać ustawienia do aplikacji.");
            }
            else
            {
                ShowInfo(
                    severity: InfoBarSeverity.Informational,
                    title: "Profile wczytane",
                    message: $"Załadowano {profiles.Count} profili z ustawień.");
            }

            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            if (ProfilesEmptyStatePanel == null || ProfilesList == null)
            {
                return;
            }

            bool hasProfiles = profiles != null && profiles.Count > 0;
            ProfilesEmptyStatePanel.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
            ProfilesList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var newProfile = new AppProfile
            {
                DisplayName = "Nowy profil",
                ExecutableFileName = string.Empty,
                AllowBackgroundActivation = false,
                Priority = 0,
                BrightnessPercent = 100,
                SaturationBoost = 1.0,
                SmoothingSpeedMs = 120,
                BlackCutoffThreshold = 8,
                ColorTemperatureKelvin = 6500,
                GammaValue = 2.2
            };

            profiles.Add(newProfile);
            SaveStatusText.Text = "Dodano nowy profil. Zapisz zmiany, aby utrwalić konfigurację.";
            ShowInfo(
                severity: InfoBarSeverity.Informational,
                title: "Dodano profil",
                message: "Dodano nowy profil. Pamiętaj o zapisaniu zmian.");
            UpdateEmptyState();
        }

        private void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AppProfile profileToRemove)
            {
                profiles.Remove(profileToRemove);

                SaveStatusText.Text = "Usunięto profil. Zapisz zmiany, aby zaktualizować konfigurację.";
                ShowInfo(
                    severity: InfoBarSeverity.Warning,
                    title: "Profil usunięty",
                    message: "Profil został usunięty. Zapisz zmiany, aby zapisać nowy stan.");
                UpdateEmptyState();
            }
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
                    severity: InfoBarSeverity.Informational,
                    title: "Aplikacja wybrana",
                    message: $"Wybrano plik: {file.Name}. Zapisz zmiany, aby utrwalić powiązanie.");
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Błąd wyboru aplikacji: {ex.Message}";
                ShowInfo(
                    severity: InfoBarSeverity.Error,
                    title: "Błąd wyboru aplikacji",
                    message: $"Wystąpił błąd podczas wyboru pliku .exe: {ex.Message}");
            }
        }

        private void ResetProfileImageDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not AppProfile profile)
            {
                return;
            }

            profile.BrightnessPercent = 100;
            profile.SaturationBoost = 1.0;
            profile.SmoothingSpeedMs = 120;
            profile.BlackCutoffThreshold = 8;
            profile.ColorTemperatureKelvin = 6500;
            profile.GammaValue = 2.2;

            SaveStatusText.Text = "Przywrócono domyślne parametry obrazu dla profilu. Zapisz zmiany, aby je utrwalić.";
            ShowInfo(
                severity: InfoBarSeverity.Informational,
                title: "Domyślne parametry",
                message: "Parametry obrazu dla tego profilu zostały zresetowane do wartości domyślnych.");
        }

        private void SaveChangesButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null)
            {
                SaveStatusText.Text = "Brak dostępu do ustawień aplikacji.";
                ShowInfo(
                    severity: InfoBarSeverity.Error,
                    title: "Brak ustawień",
                    message: "Brak dostępu do ustawień aplikacji. Zamknij i uruchom ponownie AmbilightEngine.");
                return;
            }

            try
            {
                mainWindow.Settings.Profiles = new List<AppProfile>(profiles);
                mainWindow.SettingsService.Save(mainWindow.Settings);

                // Odśwież listę profili w silniku, jeśli host to obsługuje.
                mainWindow.EngineHost.RefreshProfileList();

                SaveStatusText.Text = "Zapisano zmiany w profilach.";
                ShowInfo(
                    severity: InfoBarSeverity.Success,
                    title: "Zapisano profile",
                    message: "Zmiany w profilach zostały zapisane i przekazane do silnika.");
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = $"Błąd zapisu: {ex.Message}";
                ShowInfo(
                    severity: InfoBarSeverity.Error,
                    title: "Błąd zapisu",
                    message: $"Nie udało się zapisać profili: {ex.Message}");
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