using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;

namespace AmbilightEngine.Pages
{
    public sealed partial class DashboardPage : Page
    {
        private MainWindow? mainWindow;
        private DispatcherQueueTimer? fpsTimer;

        public DashboardPage()
        {
            InitializeComponent();
            this.Loaded += DashboardPage_Loaded;
        }

        private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            mainWindow = (Application.Current as App)?.MainAppWindow;
            if (mainWindow == null) return;

            mainWindow.EngineHost.StatusChanged += OnStatusChanged;

            fpsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            fpsTimer.Interval = TimeSpan.FromSeconds(1);
            fpsTimer.Tick += (s, args) =>
            {
                if (mainWindow.EngineHost.IsRunning)
                {
                    FpsText.Text = $"FPS: przechwytywanie {mainWindow.EngineHost.CaptureFps:F0} / wysyłanie {mainWindow.EngineHost.SendFps:F0}";
                }
            };
            fpsTimer.Start();
        }

        private void OnStatusChanged(string status)
        {
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                StatusText.Text = $"Status: {status}";
            });
        }

        private async void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (mainWindow == null) return;

            if (mainWindow.EngineHost.IsRunning)
            {
                mainWindow.EngineHost.Stop();
                ToggleButton.Content = "Wybierz monitor i uruchom Ambilight";
            }
            else
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(mainWindow);
                bool started = await mainWindow.EngineHost.StartAsync(hwnd);
                if (started)
                {
                    ToggleButton.Content = "Zatrzymaj Ambilight";
                }
            }
        }
    }
}