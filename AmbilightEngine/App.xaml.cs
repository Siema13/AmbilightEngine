using Microsoft.UI.Xaml;

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
            m_window = new MainWindow();
            MainAppWindow = m_window as MainWindow;
            m_window.Activate();
        }
    }
}