using Microsoft.UI.Xaml;
using System;
using AmbilightEngine.Core.SystemState;

namespace AmbilightEngine
{
    public partial class App : Application
    {
        private Window? m_window;

        public MainWindow? MainAppWindow { get; private set; }

        public App()
        {
            // KLUCZOWE: musi być wywołane jak najwcześniej, zanim Windows zdąży
            // zastosować Efficiency Mode / EcoQoS throttling do tego procesu.
            // Bez tego System.Threading.Timer w SystemStateWatcher (i inne timery)
            // są drastycznie spowalniane lub zamrażane po zablokowaniu ekranu,
            // co uniemożliwia wykrycie blokady i aktywację trybu ambientowego.
            ProcessPowerThrottling.DisableThrottling();

            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
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
        }
    }
}