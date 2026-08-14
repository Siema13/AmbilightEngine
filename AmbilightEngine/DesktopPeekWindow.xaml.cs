using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AmbilightEngine;

// Malutkie, zawsze-na-wierzchu okienko-kotwica pokazywane podczas oceny pulpitu
// w kroku weryfikacji kalibratora - jedyny sposób, żeby wrócić do fullscreen
// bez szukania zminimalizowanego okna na pasku zadań.
public sealed partial class DesktopPeekWindow : Window
{
    private readonly CalibrationOverlayWindow owner;

    public DesktopPeekWindow(CalibrationOverlayWindow owner)
    {
        InitializeComponent();
        this.owner = owner;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new SizeInt32(360, 70));
        appWindow.MoveInZOrderAtTop();

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
    }

    private void ReturnButton_Click(object sender, RoutedEventArgs e)
    {
        owner.RestoreFromDesktopPeek();
    }
}