using System;
using System.Runtime.InteropServices;
using AmbilightEngine.Core.SystemState;
using AmbilightEngine.Models;
using AmbilightEngine.Services;
using Microsoft.UI.Xaml;

namespace AmbilightEngine
{
    public partial class App : Application
    {
        private const uint MbOk = 0x00000000;
        private const uint MbIconInformation = 0x00000040;

        private readonly SingleInstanceManager singleInstanceManager = new();
        private Window? m_window;

        private SettingsStorageService? settingsStorageService;
        private GlobalHotkeyService? GlobalHotkeyService;

        public MainWindow? MainAppWindow { get; private set; }

        public App()
        {
            ProcessPowerThrottling.DisableThrottling();
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (!singleInstanceManager.TryAcquire())
            {
                MessageBox(
                    IntPtr.Zero,
                    "AmbilightEngine jest już uruchomiony.\n\nSprawdź ikonę w zasobniku systemowym.",
                    "AmbilightEngine",
                    MbOk | MbIconInformation);

                Environment.Exit(0);
                return;
            }

            var mainWindow = new MainWindow();
            MainAppWindow = mainWindow;
            m_window = mainWindow;

            string[] launchArgs = Environment.GetCommandLineArgs();
            bool startHidden = mainWindow.ShouldStartMinimizedToTray(launchArgs);

            m_window.Activate();

            if (startHidden)
            {
                mainWindow.StartHiddenToTray();
            }

            try
            {
                settingsStorageService = new SettingsStorageService();
                GlobalHotkeyService = new GlobalHotkeyService(mainWindow);
                GlobalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;

                var hotkeySettings = settingsStorageService.LoadHotkeySettings();
                GlobalHotkeyService.LoadFromSettings(hotkeySettings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Nie udało się zainicjalizować GlobalHotkeyService: {ex.Message}");
            }

            m_window.Closed += OnMainWindowClosed;
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            GlobalHotkeyService?.Dispose();
            GlobalHotkeyService = null;
        }

        private void OnGlobalHotkeyPressed(string actionId)
        {
            if (MainAppWindow is null)
            {
                return;
            }

            switch (actionId)
            {
                case HotkeyActionIds.ToggleEngine:
                    MainAppWindow.ToggleCommand.Execute(null);
                    break;

                case HotkeyActionIds.Blackout:
                    _ = MainAppWindow.EngineHost.ActivateStaticColorAsync(0, 0, 0);
                    break;

                // CycleMode, BrightnessUp/Down i CycleWhitePreset wymagają dodatkowych
                // metod w AppEngineHost/AmbilightSettings, których jeszcze nie mamy —
                // patrz sekcja "Do doprecyzowania" w odpowiedzi.
                case HotkeyActionIds.CycleMode:
                case HotkeyActionIds.BrightnessUp:
                case HotkeyActionIds.BrightnessDown:
                case HotkeyActionIds.CycleWhitePreset:
                    System.Diagnostics.Debug.WriteLine(
                        $"DIAG: Akcja skrótu '{actionId}' nie jest jeszcze zaimplementowana w EngineHost.");
                    break;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(
            IntPtr hWnd,
            string text,
            string caption,
            uint type);
    }
}