using Microsoft.UI.Xaml;
using System;

namespace AmbilightEngine
{
    public partial class App : Application
    {
        private Window? m_window;

        public MainWindow? MainAppWindow { get; private set; }

        public App()
        {
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