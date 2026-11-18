using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace EliteScriptStudio.Desktop;

// The UI thread owns this window. The media worker releases its video overlay before Destroy.
internal sealed class DesktopXboxPlayer
{
    private readonly AppWindow _window;
    internal nint Handle { get; }
    internal event Action? CloseRequested;

    internal DesktopXboxPlayer(WindowId owner)
    {
        var area = DisplayArea.GetFromWindowId(owner, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(1280, (int)(area.Width * .85));
        var height = Math.Min((int)(area.Height * .85), width * 9 / 16 + 40);
        _window = AppWindow.Create(OverlappedPresenter.Create(), owner);
        try
        {
            _window.Title = "Xbox Remote Play";
            _window.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
            Handle = Win32Interop.GetWindowFromWindowId(_window.Id);
            _window.Closing += Closing;
        }
        catch { _window.Destroy(); throw; }
    }

    internal void Show()
    {
        if (_window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        _window.Show(true);
    }

    private void Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        _window.Hide();
        CloseRequested?.Invoke();
    }

    internal void Destroy()
    {
        _window.Closing -= Closing;
        _window.Destroy();
    }
}
