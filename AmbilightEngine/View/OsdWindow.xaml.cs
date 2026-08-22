using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AmbilightEngine.Views
{
    /// <summary>
    /// Bezramkowe, zawsze-na-wierzchu okno OSD (on-screen display) pokazujące krótkie
    /// potwierdzenie po użyciu skrótu globalnego (zmiana trybu, Blackout, jasność,
    /// preset bieli, uruchomienie sceny Quick Palette). Nie przechwytuje fokusu ani
    /// kliknięć - działa czysto informacyjnie, wycentrowane u dołu ekranu głównego.
    ///
    /// Cykl życia (pokaż / auto-ukryj) jest sterowany z zewnątrz przez OsdNotificationService,
    /// żeby to okno pozostawało proste i nie zarządzało własnymi timerami.
    /// </summary>
    public sealed partial class OsdWindow : Window
    {
        // Zmniejszone wymiary - kompaktowa "pastylka" zamiast dużego panelu.
        private const int OsdWidth = 300;
        private const int OsdHeight = 56;
        private const int BottomMarginPixels = 120;

        public OsdWindow()
        {
            InitializeComponent();
            ConfigureWindowStyle();
        }

        private void ConfigureWindowStyle()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            appWindow.IsShownInSwitchers = false;
            appWindow.Resize(new Windows.Graphics.SizeInt32(OsdWidth, OsdHeight));

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            }

            // Klik-przez-okno: OSD nie może przechwytywać wejścia myszy/klawiatury,
            // żeby nie zakłócać gry/aplikacji działającej pod spodem w trybie pełnoekranowym.
            ApplyClickThrough(hwnd);

            CenterOnPrimaryDisplayBottom(appWindow);
        }

        private static void CenterOnPrimaryDisplayBottom(AppWindow appWindow)
        {
            DisplayArea? displayArea = DisplayArea.Primary;

            if (displayArea is null)
            {
                return;
            }

            var workArea = displayArea.WorkArea;

            int x = workArea.X + (workArea.Width - OsdWidth) / 2;
            int y = workArea.Y + workArea.Height - OsdHeight - BottomMarginPixels;

            appWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private static void ApplyClickThrough(IntPtr hwnd)
        {
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        public void SetContent(string glyph, string title, string? subtitle)
        {
            OsdIcon.Glyph = glyph;
            OsdTitleText.Text = title;

            if (string.IsNullOrWhiteSpace(subtitle))
            {
                OsdSubtitleText.Visibility = Visibility.Collapsed;
                OsdSubtitleText.Text = string.Empty;
            }
            else
            {
                OsdSubtitleText.Text = subtitle;
                OsdSubtitleText.Visibility = Visibility.Visible;
            }
        }

        public void ShowOsd()
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            appWindow.Show(activateWindow: false);
            FadeInStoryboard.Begin();
        }

        public void HideOsd()
        {
            // Odtwarzamy zanikanie, a rzeczywiste ukrycie okna dopiero po zakończeniu
            // animacji - inaczej Hide() przycięłoby efekt fade w połowie.
            void OnFadeOutCompleted(object? sender, object e)
            {
                FadeOutStoryboard.Completed -= OnFadeOutCompleted;

                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

                appWindow.Hide();
            }

            FadeOutStoryboard.Completed += OnFadeOutCompleted;
            FadeOutStoryboard.Begin();
        }
    }
}