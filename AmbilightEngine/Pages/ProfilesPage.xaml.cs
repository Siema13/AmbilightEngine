using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
        private bool isLoadingWledLists;

        // Listy współdzielone przez wszystkie ComboBox efektów/presetów na stronie -
        // DefaultEffectComboBox/DefaultPresetComboBox są "źródłem prawdy", a każdy
        // ComboBox w ItemsControl (lista profili) bindem ElementName pobiera z nich
        // ItemsSource, więc odświeżenie listy raz aktualizuje wszystkie kontrolki naraz.
        private List<string> loadedWledEffectNames = new();
        private List<AmbilightEngine.Core.Models.WledPresetInfo> loadedWledPresets = new();

        public ProfilesPage()
        {
            InitializeComponent();
            Loaded += ProfilesPage_Loaded;
            Unloaded += ProfilesPage_Unloaded;
        }

        private async void ProfilesPage_Loaded(object sender, RoutedEventArgs e)
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
                RefreshHotkeyLabels();
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

            // NOWOŚĆ: automatyczne wczytanie list efektów i presetów WLED przy
            // otwarciu strony, żeby ComboBox-y nie były puste od startu. Przycisk
            // "Odśwież listy WLED" zostaje jako opcja ręcznego ponownego pobrania
            // (np. po dodaniu nowego presetu w aplikacji webowej WLED).
            await RefreshWledListsAsync();
        }

        private async Task RefreshWledListsAsync()
        {
            if (mainWindow == null)
            {
                return;
            }

            isLoadingWledLists = true;
            RefreshWledListsButton.IsEnabled = false;

            try
            {
                List<string> effects = await mainWindow.EngineHost.GetAvailableWledEffectsAsync();
                loadedWledEffectNames = effects
                    .Where(name => !string.Equals(name, "RSVD", StringComparison.OrdinalIgnoreCase) &&
                                   name.Trim() != "-")
                    .ToList();

                DefaultEffectComboBox.ItemsSource = loadedWledEffectNames;

                var presetService = new AmbilightEngine.Core.Hardware.WledPresetService();
                loadedWledPresets = await presetService.GetPresetsAsync(mainWindow.Settings.EspIpAddress);

                DefaultPresetComboBox.ItemsSource = loadedWledPresets;

                if (loadedWledEffectNames.Count == 0 && loadedWledPresets.Count == 0)
                {
                    ShowInfo(
                        InfoBarSeverity.Warning,
                        "WLED niedostępne",
                        "Nie udało się połączyć z urządzeniem WLED. Sprawdź adres IP i połączenie sieciowe, następnie kliknij „Odśwież listy WLED”.");
                }
                else
                {
                    ShowInfo(
                        InfoBarSeverity.Success,
                        "Listy WLED wczytane",
                        $"Wczytano {loadedWledEffectNames.Count} efektów i {loadedWledPresets.Count} presetów/playlist z urządzenia WLED.");
                }

                RestoreSelectedEffectAndPreset();
            }
            catch (Exception ex)
            {
                ShowInfo(
                    InfoBarSeverity.Error,
                    "Błąd wczytywania list WLED",
                    $"Nie udało się pobrać efektów/presetów z urządzenia: {ex.Message}");
            }
            finally
            {
                isLoadingWledLists = false;
                RefreshWledListsButton.IsEnabled = true;
            }
        }

        // Po wczytaniu list dopasowuje aktualnie zapisany WledEffectId/WledPresetId
        // profilu domyślnego do pozycji w ComboBox - bez tego ComboBox pokazywałby
        // puste pole mimo istniejącej, poprawnej wartości w ustawieniach.
        private void RestoreSelectedEffectAndPreset()
        {
            if (mainWindow == null)
            {
                return;
            }

            AppProfile defaultProfile = mainWindow.Settings.DefaultProfile;

            if (defaultProfile.WledEffectId >= 0 && defaultProfile.WledEffectId < loadedWledEffectNames.Count)
            {
                DefaultEffectComboBox.SelectedIndex = defaultProfile.WledEffectId;
            }

            int defaultPresetIndex = loadedWledPresets.FindIndex(p => p.PresetId == defaultProfile.WledPresetId);
            if (defaultPresetIndex >= 0)
            {
                DefaultPresetComboBox.SelectedIndex = defaultPresetIndex;
            }
        }

        private async void RefreshWledListsButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshWledListsAsync();
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
                RefreshHotkeyLabels();
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

        private void ActionTypeWledPreset_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Tag is AppProfile profile)
            {
                profile.ActionType = ProfileActionType.WledPreset;
                SaveStatusText.Text =
                    "Zmieniono typ akcji profilu na preset WLED. Zapisz zmiany, aby utrwalić konfigurację.";
            }
        }

        // NOWOŚĆ: ComboBox efektu WLED w wierszach profili z listy jest bindowany
        // SelectedIndex="{Binding WledEffectId, Mode=TwoWay}" - wybór zapisuje się
        // od razu w modelu bez potrzeby osobnego handlera SelectionChanged.

        // NOWOŚĆ: ComboBox presetu w wierszach profili wymaga własnego handlera,
        // bo WledPresetInfo.PresetId (nie indeks pozycji) musi trafić do profile.WledPresetId.
        private void ProfilePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingWledLists ||
                sender is not ComboBox comboBox ||
                comboBox.Tag is not AppProfile profile ||
                comboBox.SelectedItem is not AmbilightEngine.Core.Models.WledPresetInfo preset)
            {
                return;
            }

            profile.WledPresetId = preset.PresetId;
            SaveStatusText.Text = "Zmieniono preset WLED profilu. Zapisz zmiany, aby utrwalić konfigurację.";
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

        private void DefaultActionTypeWledPreset_Checked(object sender, RoutedEventArgs e)
        {
            if (mainWindow?.Settings.DefaultProfile is not AppProfile profile)
            {
                return;
            }

            profile.ActionType = ProfileActionType.WledPreset;
            SaveStatusText.Text =
                "Profil domyślny będzie aktywował preset WLED. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void DefaultEffectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingWledLists || mainWindow?.Settings.DefaultProfile is not AppProfile profile ||
                DefaultEffectComboBox.SelectedIndex < 0)
            {
                return;
            }

            profile.WledEffectId = DefaultEffectComboBox.SelectedIndex;
            SaveStatusText.Text = "Zmieniono efekt WLED profilu domyślnego. Zapisz zmiany, aby utrwalić konfigurację.";
        }

        private void DefaultPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoadingWledLists || mainWindow?.Settings.DefaultProfile is not AppProfile profile ||
                DefaultPresetComboBox.SelectedItem is not AmbilightEngine.Core.Models.WledPresetInfo preset)
            {
                return;
            }

            profile.WledPresetId = preset.PresetId;
            SaveStatusText.Text = "Zmieniono preset WLED profilu domyślnego. Zapisz zmiany, aby utrwalić konfigurację.";
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

        private void RefreshHotkeyLabels()
        {
            if (mainWindow == null)
            {
                return;
            }

            foreach (AppProfile profile in profiles)
            {
                profile.AssignedHotkeyLabel = BuildHotkeyLabelForProfile(profile.ProfileId);
            }

            if (mainWindow.Settings.DefaultProfile is AppProfile defaultProfile)
            {
                defaultProfile.AssignedHotkeyLabel =
                    BuildHotkeyLabelForProfile(defaultProfile.ProfileId);
            }
        }

        private string BuildHotkeyLabelForProfile(string profileId)
        {
            const string NoHotkeyLabel = "Brak przypisanego skrótu";

            if (mainWindow?.Settings?.Hotkeys?.Bindings == null || string.IsNullOrWhiteSpace(profileId))
            {
                return NoHotkeyLabel;
            }

            string expectedActionId = $"profile.activate:{profileId}";

            AmbilightEngine.Models.HotkeyBinding? binding = mainWindow.Settings.Hotkeys.Bindings
                .FirstOrDefault(b => string.Equals(b.ActionId, expectedActionId, StringComparison.Ordinal));

            if (binding == null || !binding.IsAssigned)
            {
                return NoHotkeyLabel;
            }

            return binding.ToDisplayString();
        }
    }
}