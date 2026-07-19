using System;
using System.Windows.Input;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Pages;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace AmbilightEngine
{
    public sealed partial class MainWindow : Window
    {
        private MicaController? micaController;
        private SystemBackdropConfiguration? backdropConfiguration;
        private bool isExitRequested = false;

        public AmbilightSettings Settings { get; }
        public SettingsService SettingsService { get; }
        public AppEngineHost EngineHost { get; }

        public ICommand ShowCommand { get; }
        public ICommand ToggleCommand { get; }
        public ICommand ExitCommand { get; }

        public MainWindow()
        {
            InitializeComponent();

            ShowCommand = new RelayCommand(_ => RestoreWindow());
            ToggleCommand = new RelayCommand(_ => ToggleAmbilight());
            ExitCommand = new RelayCommand(_ => ExitApplication());

            SettingsService = new SettingsService();
            Settings = SettingsService.Load();
            EngineHost = new AppEngineHost(Settings);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            TrySetMicaBackdrop();

            this.Closed += MainWindow_Closed;

            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(DashboardPage));

            this.Activated += MainWindow_Activated;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= MainWindow_Activated;
            TrayIcon.ForceCreate();
            System.Diagnostics.Debug.WriteLine("[DIAG] TrayIcon.ForceCreate() wywołane po aktywacji okna");
        }

        private void TrySetMicaBackdrop()
        {
            if (!MicaController.IsSupported()) return;

            backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true
            };

            micaController = new MicaController();
            micaController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            micaController.SetSystemBackdropConfiguration(backdropConfiguration);
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                string? tag = item.Tag?.ToString();

                if (tag == "dashboard")
                {
                    ContentFrame.Navigate(typeof(DashboardPage));
                }
                else if (tag == "settings")
                {
                    ContentFrame.Navigate(typeof(SettingsPage));
                }
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (!isExitRequested)
            {
                args.Handled = true;
                this.AppWindow.Hide();
                return;
            }

            SettingsService.Save(Settings);
            EngineHost.Dispose();
            micaController?.Dispose();
            TrayIcon.Dispose();
        }

        private void RestoreWindow()
        {
            ShowAndFocusWindow();
        }

        // Wspólna logika przywracania okna: pokazuje je, wymusza pierwszy plan,
        // resetuje nawigację na Dashboard (niezależnie od tego, gdzie użytkownik był wcześniej).
        private void ShowAndFocusWindow()
        {
            this.AppWindow.Show();
            this.AppWindow.MoveInZOrderAtTop();

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            PInvokeHelpers.ForceForegroundWindow(hwnd);

            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(DashboardPage));

            this.Activate();
        }

        private async void ToggleAmbilight()
        {
            try
            {
                if (EngineHost.IsRunning)
                {
                    EngineHost.Stop();
                    System.Diagnostics.Debug.WriteLine("[DIAG] Ambilight zatrzymany.");
                }
                else
                {
                    IntPtr hwnd = WindowNative.GetWindowHandle(this);
                    bool started = await EngineHost.StartAsync(hwnd);

                    if (started)
                    {
                        System.Diagnostics.Debug.WriteLine("[DIAG] Ambilight wystartował poprawnie.");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[DIAG] StartAsync zwrócił false - nie wybrano monitora lub wystąpił błąd inicjalizacji.");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DIAG] Wyjątek w ToggleAmbilight: {ex.Message}");
            }
        }

        private void ExitApplication()
        {
            isExitRequested = true;
            this.Close();
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