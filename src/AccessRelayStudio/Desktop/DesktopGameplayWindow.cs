using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace EliteScriptStudio.Desktop;

/// <summary>A movable view of the owner's session. Owns no capture, inference or controller worker.</summary>
internal sealed class DesktopGameplayWindow : Window
{
    private const string Url = "https://studio.local/index.html?surface=gameplay";
    private readonly WebView2 _browser = new();
    private readonly Grid _root = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<object> _status;
    private readonly Func<Task> _stop;
    private readonly Action _attach;
    private readonly Func<string, DesktopMedia?> _media;
    private readonly HashSet<string> _requests = new(StringComparer.Ordinal);
    private CoreWebView2? _web;
    private bool _ready, _closed;
    private string _theme = "light";

    internal DesktopGameplayWindow(Func<object> status, Func<Task> stop, Action attach, Func<string, DesktopMedia?> media)
    {
        _status = status; _stop = stop; _attach = attach; _media = media;
        Title = "Live Gameplay — AccessRelay Studio";
        _root.Children.Add(_browser); Content = _root;
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AccessRelayStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(1100, display.Width - 24), Math.Min(750, display.Height - 24)));
        if (AppWindow.Presenter is OverlappedPresenter sizing)
        { sizing.PreferredMinimumWidth = 480; sizing.PreferredMinimumHeight = 320; }
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => PublishStatus();
        AppWindow.Changed += (_, _) => { PresentationChanged?.Invoke(); PublishStatus(); };
        Closed += (_, _) => { _closed = true; _ready = false; _timer.Stop(); _browser.Close(); };
    }

    internal event Action? PresentationChanged;
    internal bool IsPresenting => _ready && !_closed && AppWindow.IsVisible &&
        (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Minimized);

    internal async Task OpenAsync(CoreWebView2Environment environment, string theme)
    {
        SetTheme(theme); Activate();
        await _browser.EnsureCoreWebView2Async(environment);
        if (_closed) return;
        var core = _web = _browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = DesktopEnvironment.IsVerification;
        core.Settings.AreDefaultContextMenusEnabled = core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = core.Settings.AreHostObjectsAllowed = core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.SetVirtualHostNameToFolderMapping("studio.local", Path.Combine(AppContext.BaseDirectory, "DesktopUi"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, args) => args.Cancel = !TrustedPage(args.Uri);
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += MessageReceivedAsync;
        core.AddWebResourceRequestedFilter("https://media.accessrelay.test/vision/preview*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += async (sender, args) => await DesktopMediaResponse.SendAsync(sender, args,
            uri => !_closed && uri.AbsolutePath == "/vision/preview" ? _media("preview") : null);
        core.NavigationCompleted += (_, args) => { if (!args.IsSuccess) Close(); };
        core.ProcessFailed += (_, args) =>
        {
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) Close();
        };
        core.Navigate(Url); _timer.Start();
    }

    private static bool TrustedPage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "studio.local" && uri.IsDefaultPort && uri.AbsolutePath == "/index.html"
        && uri.Query == "?surface=gameplay";

    internal void SetTheme(string theme)
    {
        _theme = theme;
        bool dark = theme == "dark";
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        var background = dark ? ColorHelper.FromArgb(255, 17, 23, 29) : Colors.White;
        _root.Background = new SolidColorBrush(background);
        AppWindow.TitleBar.BackgroundColor = AppWindow.TitleBar.ButtonBackgroundColor = background;
        AppWindow.TitleBar.ForegroundColor = AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        PublishStatus();
    }

    private object Status() => new { vision = _status(), theme = _theme };
    private void PublishStatus()
    {
        if (!IsPresenting) return;
        try { Post(new { type = "status", status = Status() }); }
        catch (Exception error) { Trace.TraceError("Gameplay viewer: {0}", error.Message); }
    }
    private void Post(object value)
    {
        if (!_closed) _web?.PostWebMessageAsJson(JsonSerializer.Serialize(value, DesktopJson.Options));
    }

    private async void MessageReceivedAsync(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closed || !TrustedPage(args.Source)) return;
        string? id = null; bool registered = false;
        try
        {
            if (args.WebMessageAsJson.Length > 4096) throw new InvalidDataException("The viewer request is too large.");
            using var document = JsonDocument.Parse(args.WebMessageAsJson, new JsonDocumentOptions { MaxDepth = 8 });
            var request = document.RootElement;
            if (request.Number("protocol") != 1) throw new InvalidDataException("The interface protocol is not supported.");
            id = request.Text("id");
            if (id.Length is < 1 or > 128 || id.Any(char.IsControl) || _requests.Count >= 8 || !_requests.Add(id))
                throw new InvalidDataException("The request ID is invalid or already pending.");
            registered = true;
            object? result = null;
            string method = request.Text("method");
            switch (method)
            {
                case "app.bootstrap": _ready = true; PresentationChanged?.Invoke(); result = Status(); break;
                case "vision.stop": await _stop(); break;
                case "app.gameplayAttach": break;
                default: throw new InvalidOperationException("This action is unavailable in the capture window.");
            }
            Post(new { type = "response", id, ok = true, result });
            if (method == "app.gameplayAttach") DispatcherQueue.TryEnqueue(() => _attach());
            else PublishStatus();
        }
        catch (Exception error) { if (!_closed) Post(new { type = "response", id, ok = false, error = error.Message }); }
        finally { if (registered && id is not null) _requests.Remove(id); }
    }
}
