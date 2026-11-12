using Microsoft.UI.Xaml;

namespace EliteScriptStudio;

public partial class App : Microsoft.UI.Xaml.Application
{
    private DesktopWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new DesktopWindow();
        _window.Activate();
    }
}
