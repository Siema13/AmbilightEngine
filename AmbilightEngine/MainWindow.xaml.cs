using System;
using System.Linq;
using System.Windows.Input;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Pages;
using AmbilightEngine.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinRT.Interop;

namespace AmbilightEngine
{
    public sealed partial class MainWindow : Window
    {
        private readonly StartupRegistrationService startupRegistrationService = new();
        private MicaController? micaController;
        private SystemBackdropConfiguration? backdropConfiguration;
        private bool isExitRequested;
        private bool isWindowVisible = true;

        public AmbilightSettings Settings { get; }
        public SettingsService SettingsService { get; }
        public AppEngineHost EngineHost { get; }
        public ISettingsApplyService SettingsApplyService { get; }
        public IWledDiagnosticsService WledDiagnosticsService { get; }
        public ICommand ShowCommand { get; }
        public ICommand ToggleWindowVisibilityCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ToggleCommand { get; }
        public ICommand ExitCommand { get; }
        public Services.DynamicThemeService ThemeService { get; } = new Services.DynamicThemeService();

        public MainWindow()
        {
            InitializeComponent();

            ShowCommand = new RelayCommand(() => RestoreWindow());
            ToggleWindowVisibilityCommand = new RelayCommand(() => ToggleWindowVisibility());
            OpenSettingsCommand = new RelayCommand(() => OpenSettingsFromTray());
            StartCommand = new RelayCommand(async () => await StartAmbilightAsync());
            StopCommand = new RelayCommand(() => StopAmbilight());
            ToggleCommand = new RelayCommand(async () => await ToggleAmbilightAsync());
            ExitCommand = new RelayCommand(() => ExitApplication());

            SettingsService = new SettingsService();
            Settings = SettingsService.Load();
            bool isDarkMode = Application.Current.RequestedTheme == ApplicationTheme.Dark;

            if (Settings.UseCustomTheme)
            {
                ThemeService.ApplyCustomTheme(Settings, isDarkMode);
            }
            else
            {
                ThemeService.ApplyTheme(Settings.AccentThemeName, isDarkMode);
            }

            EngineHost = new AppEngineHost(Settings);
            SettingsApplyService = new SettingsApplyService(SettingsService, EngineHost);
            WledDiagnosticsService = new WledDiagnosticsService();

            startupRegistrationService.Apply(Settings.StartWithWindows, Settings.StartMinimizedToTray);

            EngineHost.StatusChanged += EngineHost_StatusChanged;
            UpdateGlobalStatus(EngineHost.CurrentStatus);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Mica jest domyślnie aktywne tylko wtedy, gdy użytkownik nie korzysta z motywu niestandardowego.
            // Motyw niestandardowy wymaga jednolitego, w pełni kontrolowanego koloru tła okna,
            // a Mica z definicji nadpisuje go systemowym, rozmytym efektem.
            UpdateBackdropForCustomTheme(Settings.UseCustomTheme);

            Closed += MainWindow_Closed;
            Activated += MainWindow_Activated;

            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(DashboardPage));
            UpdateTrayToolTip(EngineHost.CurrentStatus);

            // NOWOŚĆ: połączenie z WLED (JSON API + DDP) startuje automatycznie razem
            // z aplikacją, niezależnie od tego, czy przechwytywanie ekranu jest aktywne.
            // Dzięki temu podgląd na żywo (WLED Peek) w Dashboard i Ustawieniach działa
            // od razu po otwarciu aplikacji, bez konieczności klikania "Start" na Dashboard.
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            _ = InitializeWledConnectionOnStartupAsync(hwnd);

            // NOWOŚĆ: przy pierwszym uruchomieniu aplikacji (flaga HasCompletedCalibrationOnboarding
            // == false) automatycznie otwiera wizard kalibracji, żeby nowy użytkownik od razu
            // trafiał na dobrze dostrojony obraz. Odpalane raz, po pierwszej aktywacji okna -
            // odpina się od Activated natychmiast po pierwszym wywołaniu.
            Activated += MainWindow_FirstActivationCheckOnboarding;
        }

        private async System.Threading.Tasks.Task InitializeWledConnectionOnStartupAsync(IntPtr hwnd)
        {
            try
            {
                bool connected = await EngineHost.EnsureWledConnectionAsync(hwnd);

                System.Diagnostics.Debug.WriteLine(
                    connected
                        ? "DIAG: Połączenie z WLED nawiązane automatycznie przy starcie aplikacji."
                        : "DIAG: Nie udało się automatycznie połączyć z WLED przy starcie.");

                if (!connected || !Settings.AutoStartAmbilight)
                {
                    return;
                }

                bool started = await StartConfiguredAutoStartModeAsync(hwnd);

                System.Diagnostics.Debug.WriteLine(
                    started
                        ? $"DIAG: Automatycznie uruchomiono tryb: {Settings.AutoStartDisplayMode}."
                        : $"DIAG: Nie udało się automatycznie uruchomić trybu: {Settings.AutoStartDisplayMode}.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DIAG: Wyjątek podczas automatycznego uruchamiania Ambilight: {ex.Message}");
            }
        }
        private async System.Threading.Tasks.Task<bool> StartConfiguredAutoStartModeAsync(IntPtr hwnd)
        {
            try
            {
                switch (Settings.AutoStartDisplayMode)
                {
                    case DisplayMode.StaticColor:
                        return await EngineHost.ActivateStaticColorAsync(
                            Settings.StaticColorR,
                            Settings.StaticColorG,
                            Settings.StaticColorB);

                    case DisplayMode.WledEffects:
                        var primaryColor = (
                            Settings.LastWledPrimaryColorR,
                            Settings.LastWledPrimaryColorG,
                            Settings.LastWledPrimaryColorB);

                        var secondaryColor = (
                            Settings.LastWledSecondaryColorR,
                            Settings.LastWledSecondaryColorG,
                            Settings.LastWledSecondaryColorB);

                        return await EngineHost.ActivateWledEffectAsync(
                            Settings.LastWledEffectId,
                            Settings.LastWledSpeed,
                            Settings.LastWledIntensity,
                            Settings.LastWledPaletteId,
                            primaryColor,
                            secondaryColor,
                            Settings.LastWledBrightness,
                            Settings.LastWledCustom1,
                            Settings.LastWledCustom2,
                            Settings.LastWledCustom3,
                            Settings.LastWledCheck1,
                            Settings.LastWledCheck2,
                            Settings.LastWledCheck3);

                    case DisplayMode.VideoSync:
                    default:
                        return await EngineHost.StartCaptureAsync(hwnd);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DIAG: Błąd podczas uruchamiania trybu autostartu: {ex.Message}");

                return false;
            }
        }
        public bool ShouldStartMinimizedToTray(string[] args)
        {
            bool hasTrayArgument = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            return Settings.StartMinimizedToTray || hasTrayArgument;
        }

        public void NavigateToSettings()
        {
            NavView.SelectedItem = NavView.MenuItems[2];
            ContentFrame.Navigate(typeof(SettingsPage));
        }

        public void NavigateToProfiles()
        {
            NavView.SelectedItem = NavView.MenuItems[1];
            ContentFrame.Navigate(typeof(ProfilesPage));
        }

        // NOWOŚĆ: nawigacja programowa do wizarda kalibracji (używana przez onboarding
        // przy pierwszym starcie). Szuka pozycji menu po Tag, więc jest odporna na to,
        // w którym miejscu listy dodałeś NavigationViewItem z Tag="calibration_wizard".
        public void NavigateToCalibrationWizard()
        {
            foreach (var menuItem in NavView.MenuItems)
            {
                if (menuItem is NavigationViewItem navItem &&
                    string.Equals(navItem.Tag?.ToString(), "calibration_wizard", StringComparison.Ordinal))
                {
                    NavView.SelectedItem = navItem;
                    break;
                }
            }

            ContentFrame.Navigate(typeof(CalibrationWizardPage));
        }

        public void StartHiddenToTray()
        {
            HideWindowToTray();
        }

        // Przełącza pomiędzy systemowym efektem Mica a jednolitym, niestandardowym tłem okna.
        // Wywoływane przy starcie aplikacji oraz za każdym razem, gdy użytkownik zmienia
        // ustawienie "Użyj własnego motywu", kolor tła okna lub styl tła w Ustawieniach ogólnych.
        public void UpdateBackdropForCustomTheme(bool useCustomTheme)
        {
            if (useCustomTheme)
            {
                DisableMicaBackdrop();
                RootGrid.Background = BuildCustomBackgroundBrush();
            }
            else
            {
                RootGrid.Background = (Brush)Application.Current.Resources["M3WindowBackgroundBrush"];
                TrySetMicaBackdrop();
            }
        }

        private Brush BuildCustomBackgroundBrush()
        {
            var baseColor = Windows.UI.Color.FromArgb(
                255,
                Settings.CustomWindowBackgroundR,
                Settings.CustomWindowBackgroundG,
                Settings.CustomWindowBackgroundB);

            var accentColor = Windows.UI.Color.FromArgb(
                255,
                Settings.CustomBackgroundAccentR,
                Settings.CustomBackgroundAccentG,
                Settings.CustomBackgroundAccentB);

            return Settings.CustomBackgroundStyle switch
            {
                "PureDark" => new SolidColorBrush(Darken(baseColor, 0.72)),

                "SoftGradient" => new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = baseColor, Offset = 0 },
                        new GradientStop { Color = Darken(baseColor, 0.35), Offset = 1 }
                    }
                },

                "AmbientHalo" => new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop { Color = Lighten(baseColor, 0.15), Offset = 0 },
                        new GradientStop { Color = baseColor, Offset = 0.6 },
                        new GradientStop { Color = Darken(baseColor, 0.4), Offset = 1 }
                    }
                },

                "Graphite" => new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 0.4),
                    GradientStops =
                    {
                        new GradientStop { Color = Desaturate(Lighten(baseColor, 0.08), 0.6), Offset = 0 },
                        new GradientStop { Color = Desaturate(baseColor, 0.7), Offset = 0.5 },
                        new GradientStop { Color = Desaturate(Darken(baseColor, 0.3), 0.5), Offset = 1 }
                    }
                },

                "DeepSpace" => new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Darken(baseColor, 0.5), Offset = 0 },
                        new GradientStop { Color = Darken(baseColor, 0.15), Offset = 1 }
                    }
                },

                "WarmDusk" => new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Darken(baseColor, 0.25), Offset = 0 },
                        new GradientStop { Color = Blend(baseColor, accentColor, 0.35), Offset = 0.7 },
                        new GradientStop { Color = Blend(baseColor, accentColor, 0.5), Offset = 1 }
                    }
                },

                "Aurora" => new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Darken(baseColor, 0.4), Offset = 0 },
                        new GradientStop { Color = Blend(baseColor, accentColor, 0.4), Offset = 0.45 },
                        new GradientStop { Color = Blend(baseColor, accentColor, 0.4), Offset = 0.75 },
                        new GradientStop { Color = Darken(baseColor, 0.3), Offset = 1 }
                    }
                },

                "Studio" => new RadialGradientBrush
                {
                    Center = new Windows.Foundation.Point(0.5, 0.35),
                    RadiusX = 0.9,
                    RadiusY = 0.9,
                    GradientStops =
                    {
                        new GradientStop { Color = Blend(baseColor, accentColor, 0.3), Offset = 0 },
                        new GradientStop { Color = baseColor, Offset = 0.55 },
                        new GradientStop { Color = Darken(baseColor, 0.3), Offset = 1 }
                    }
                },

                "ContrastLayered" => new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Lighten(baseColor, 0.1), Offset = 0 },
                        new GradientStop { Color = Lighten(baseColor, 0.1), Offset = 0.32 },
                        new GradientStop { Color = Darken(baseColor, 0.2), Offset = 0.33 },
                        new GradientStop { Color = Darken(baseColor, 0.2), Offset = 0.66 },
                        new GradientStop { Color = Darken(baseColor, 0.5), Offset = 0.67 },
                        new GradientStop { Color = Darken(baseColor, 0.5), Offset = 1 }
                    }
                },

                "VelvetGlow" => new RadialGradientBrush
                {
                    Center = new Windows.Foundation.Point(0.5, 0.5),
                    RadiusX = 0.8,
                    RadiusY = 0.8,
                    GradientStops =
                    {
                        new GradientStop { Color = Blend(baseColor, accentColor, 0.3), Offset = 0 },
                        new GradientStop { Color = Darken(baseColor, 0.45), Offset = 0.65 },
                        new GradientStop { Color = Darken(baseColor, 0.75), Offset = 1 }
                    }
                },

                _ => new SolidColorBrush(baseColor)
            };
        }

        private static Windows.UI.Color Blend(Windows.UI.Color color, Windows.UI.Color target, double t)
        {
            return Windows.UI.Color.FromArgb(
                255,
                (byte)(color.R + (target.R - color.R) * t),
                (byte)(color.G + (target.G - color.G) * t),
                (byte)(color.B + (target.B - color.B) * t));
        }

        private static Windows.UI.Color Desaturate(Windows.UI.Color color, double amount)
        {
            byte gray = (byte)(color.R * 0.299 + color.G * 0.587 + color.B * 0.114);
            return Windows.UI.Color.FromArgb(
                255,
                (byte)(color.R + (gray - color.R) * amount),
                (byte)(color.G + (gray - color.G) * amount),
                (byte)(color.B + (gray - color.B) * amount));
        }

        private static Windows.UI.Color Darken(Windows.UI.Color color, double amount)
        {
            return Windows.UI.Color.FromArgb(
                255,
                (byte)(color.R * (1 - amount)),
                (byte)(color.G * (1 - amount)),
                (byte)(color.B * (1 - amount)));
        }

        private static Windows.UI.Color Lighten(Windows.UI.Color color, double amount)
        {
            return Windows.UI.Color.FromArgb(
                255,
                (byte)(color.R + (255 - color.R) * amount),
                (byte)(color.G + (255 - color.G) * amount),
                (byte)(color.B + (255 - color.B) * amount));
        }

        private void EngineHost_StatusChanged(EngineStatusInfo status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateGlobalStatus(status);
                UpdateTrayToolTip(status);
            });
        }

        private void UpdateGlobalStatus(EngineStatusInfo status)
        {
            GlobalStatusText.Text = status.State switch
            {
                EngineRunState.Starting => "Uruchamianie",
                EngineRunState.Running => "Aktywny",
                EngineRunState.Ambient => "Ambient",
                EngineRunState.Error => "Błąd",
                _ => "Gotowy"
            };
        }

        private void UpdateTrayToolTip(EngineStatusInfo status)
        {
            string statusText = status.State switch
            {
                EngineRunState.Starting => "Uruchamianie",
                EngineRunState.Running => "Aktywny",
                EngineRunState.Ambient => "Ambient",
                EngineRunState.Error => "Błąd",
                _ => "Gotowy"
            };

            if (RootGrid.Resources.TryGetValue("TrayIcon", out object trayObject) &&
                trayObject is H.NotifyIcon.TaskbarIcon tray)
            {
                tray.ToolTipText = $"Ambilight Engine - {statusText}";
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= MainWindow_Activated;
            UpdateTrayToolTip(EngineHost.CurrentStatus);
        }

        // NOWOŚĆ: przy pierwszej aktywacji okna sprawdza flagę HasCompletedCalibrationOnboarding
        // i jeśli onboarding nie został jeszcze zakończony, automatycznie otwiera wizard
        // kalibracji. Odpina się od Activated natychmiast, żeby nie odpalać się powtórnie.
        private void MainWindow_FirstActivationCheckOnboarding(object sender, WindowActivatedEventArgs args)
        {
            Activated -= MainWindow_FirstActivationCheckOnboarding;

            if (!Settings.HasCompletedCalibrationOnboarding)
            {
                NavigateToCalibrationWizard();
            }
        }

        private void TrySetMicaBackdrop()
        {
            if (!MicaController.IsSupported())
            {
                return;
            }

            if (micaController is not null)
            {
                return;
            }

            backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true
            };

            micaController = new MicaController();
            micaController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            micaController.SetSystemBackdropConfiguration(backdropConfiguration);
        }

        private void DisableMicaBackdrop()
        {
            if (micaController is null)
            {
                return;
            }

            micaController.RemoveAllSystemBackdropTargets();
            micaController.Dispose();
            micaController = null;
            backdropConfiguration = null;
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
            {
                return;
            }

            string? tag = item.Tag?.ToString();
            if (tag == "dashboard")
            {
                ContentFrame.Navigate(typeof(DashboardPage));
            }
            else if (tag == "settings")
            {
                ContentFrame.Navigate(typeof(SettingsPage));
            }
            else if (tag == "profiles")
            {
                ContentFrame.Navigate(typeof(ProfilesPage));
            }
            else if (tag == "calibration_wizard")
            {
                ContentFrame.Navigate(typeof(CalibrationWizardPage));
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (!isExitRequested && Settings.CloseToTray)
            {
                args.Handled = true;
                HideWindowToTray();
                return;
            }

            SettingsService.Save(Settings);
            startupRegistrationService.Apply(Settings.StartWithWindows, Settings.StartMinimizedToTray);
            EngineHost.Dispose();
            micaController?.Dispose();
        }

        private void RestoreWindow()
        {
            ShowDashboardFromTray();
        }

        private void ToggleWindowVisibility()
        {
            if (isWindowVisible)
            {
                HideWindowToTray();
            }
            else
            {
                ShowDashboardFromTray();
            }
        }

        private void OpenSettingsFromTray()
        {
            ShowAndFocusWindow();
            isWindowVisible = true;
            NavView.SelectedItem = NavView.MenuItems[2];
            ContentFrame.Navigate(typeof(SettingsPage));
        }

        private void ShowDashboardFromTray()
        {
            ShowAndFocusWindow();
            isWindowVisible = true;
            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(DashboardPage));
        }

        private void HideWindowToTray()
        {
            AppWindow.Hide();
            isWindowVisible = false;
        }

        private void ShowAndFocusWindow()
        {
            AppWindow.Show();
            AppWindow.MoveInZOrderAtTop();

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            PInvokeHelpers.ForceForegroundWindow(hwnd);

            Activate();
        }

        // NOWOŚĆ: te trzy metody sterują TYLKO przechwytywaniem ekranu (StartCaptureAsync/
        // StopCapture) - połączenie z WLED (EngineHost.IsRunning) jest już nawiązane od
        // startu aplikacji i nie jest tu w żaden sposób dotykane.
        private async System.Threading.Tasks.Task StartAmbilightAsync()
        {
            try
            {
                if (EngineHost.IsCapturing)
                {
                    return;
                }

                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                bool started = await EngineHost.StartCaptureAsync(hwnd);
                if (started)
                {
                    System.Diagnostics.Debug.WriteLine("DIAG: Przechwytywanie wystartowało poprawnie.");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("DIAG: StartCaptureAsync zwrócił false - nie wybrano monitora lub wystąpił błąd inicjalizacji.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DIAG: Wyjątek w StartAmbilightAsync: {ex.Message}");
            }
        }

        private void StopAmbilight()
        {
            try
            {
                if (!EngineHost.IsCapturing)
                {
                    return;
                }

                EngineHost.StopCapture();
                System.Diagnostics.Debug.WriteLine("DIAG: Przechwytywanie zatrzymane, połączenie z WLED pozostaje aktywne.");
                UpdateTrayToolTip(EngineHost.CurrentStatus);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DIAG: Wyjątek w StopAmbilight: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task ToggleAmbilightAsync()
        {
            if (EngineHost.IsCapturing)
            {
                StopAmbilight();
            }
            else
            {
                await StartAmbilightAsync();
            }
        }

        private void ExitApplication()
        {
            isExitRequested = true;
            Close();
        }
    }

    internal static class PInvokeHelpers
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public static void ForceForegroundWindow(IntPtr hwnd)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }
}