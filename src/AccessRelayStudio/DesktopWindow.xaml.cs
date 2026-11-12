using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EliteScriptStudio.Audio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Desktop;
using EliteScriptStudio.Diagnostics;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace EliteScriptStudio;

/// <summary>Thin local desktop host. Web content never owns input polling, DSP, inference or release deadlines.</summary>
public sealed partial class DesktopWindow : Window
{
    private const string AppOrigin = "https://studio.local";
    private const string AppUrl = AppOrigin + "/index.html";
    private const string MediaOrigin = "https://media.accessrelay.test";
    private StudioRuntime? _runtime;
    private DesktopControllerService? _controller;
    private DesktopVisionService? _vision;
    private DesktopVoiceService? _voice;
    private readonly SystemPerformanceMonitor _performance = new();
    private readonly DispatcherQueueTimer _statusTimer;
    private readonly HashSet<string> _requests = new(StringComparer.Ordinal);
    private CoreWebView2? _web;
    private WebView2 _browser;
    private bool _replaceBrowser;
    private bool _started, _initializing, _ready, _closing, _allowClose, _clientVisible = true;
    private bool _hasUnsavedChanges, _discardClose;
    private string _page = "workspace", _startupError = "";
    private long _snapshotAt;

    public DesktopWindow()
    {
        InitializeComponent();
        _browser = DesktopWeb;
        Title = BuildInfo.WindowTitle;
        if (DesktopEnvironment.IsVerification)
        { Title += " · Verification"; DesktopBrand.Text = "AccessRelay Studio · Verification — isolated test data"; }
        ExtendsContentIntoTitleBar = true; SetTitleBar(DesktopTitleBar);
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AccessRelayStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(1536, display.Width - 24), Math.Min(960, display.Height - 24)));
        if (AppWindow.Presenter is OverlappedPresenter sizing)
        { sizing.PreferredMinimumWidth = 800; sizing.PreferredMinimumHeight = 600; }
        var title = AppWindow.TitleBar;
        title.ButtonBackgroundColor = title.ButtonInactiveBackgroundColor = Colors.Transparent;
        title.ButtonForegroundColor = title.ForegroundColor = ColorHelper.FromArgb(255, 23, 59, 84);
        title.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 207, 224, 211);
        title.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 185, 211, 196);
        title.ButtonInactiveForegroundColor = title.InactiveForegroundColor = ColorHelper.FromArgb(255, 92, 112, 108);
        _statusTimer = DispatcherQueue.CreateTimer(); _statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        _statusTimer.Tick += (_, _) => PublishStatus();
        DesktopRoot.Loaded += async (_, _) => await InitializeAsync();
        DesktopRetry.Click += async (_, _) => await InitializeAsync();
        DesktopEmergencyStop.Click += async (_, _) => { try { await StopAllAsync(); DesktopLoadingText.Text = "All processing stopped."; } catch (Exception error) { DesktopLoadingText.Text = error.Message; } };
        AppWindow.Changed += (_, _) => UpdatePresentation();
        AppWindow.Closing += ClosingAsync;
        Closed += (_, _) => { _statusTimer.Stop(); _browser.Close(); };
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _closing) return;
        _initializing = true;
        DesktopRetry.Visibility = Visibility.Collapsed;
        DesktopLoadingText.Text = "Opening your workspace…";
        try
        {
            if (!_started)
            {
                _controller ??= new DesktopControllerService(AppWindow.Id, () => _runtime, StopAllAsync);
                try
                {
                    if (_runtime is null)
                    {
                        nint hwnd = WindowNative.GetWindowHandle(this);
                        var input = new CoPilotInputMonitor(new EliteInputMonitor(hwnd), () => new EliteInputMonitor(hwnd, monitorPaddles: false));
                        var feedback = new FeedbackDispatcher(new WindowsFeedbackOutput(input));
                        try
                        {
                            feedback.Configure(_controller.FeedbackSettings);
                            _runtime = new StudioRuntime(input, _controller.ActiveProfile, _controller.Deadzone, feedback);
                        }
                        catch { feedback.Dispose(); input.Dispose(); throw; }
                        _runtime.SetScriptsEnabled(false);
                    }
                }
                catch (Exception error) { _startupError = "Controller monitoring is unavailable: " + error.Message; }
                _voice ??= new DesktopVoiceService(AppWindow.Id);
                _vision ??= new DesktopVisionService(AppWindow.Id, () => _runtime, () => _controller.ActiveProfile, () => _controller.StartOutputAsync());
                _started = true;
            }
            string assets = Path.Combine(AppContext.BaseDirectory, "DesktopUi");
            if (!File.Exists(Path.Combine(assets, "index.html")))
                throw new FileNotFoundException("The installed interface files are missing. Open AccessRelay from its complete release folder.");
            if (_web is null)
            {
                if (_replaceBrowser)
                {
                    int index = DesktopRoot.Children.IndexOf(_browser);
                    _browser.Close(); DesktopRoot.Children.Remove(_browser);
                    _browser = new WebView2(); Grid.SetRow(_browser, 1);
                    DesktopRoot.Children.Insert(index, _browser); _replaceBrowser = false;
                }
                var options = new CoreWebView2EnvironmentOptions();
                if (DesktopEnvironment.IsVerification)
                {
                    string[] args = Environment.GetCommandLineArgs();
                    int index = Array.IndexOf(args, "--ui-debug-port");
                    if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int port) && port is >= 1024 and <= 65535)
                        options.AdditionalBrowserArguments = $"--remote-debugging-port={port}";
                }
                string cache = DesktopEnvironment.DataDirectory("WebCache") ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessRelayStudio", "DesktopCache");
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, cache, options);
                await _browser.EnsureCoreWebView2Async(environment);
                if (_closing) return;
                var core = _web = _browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = DesktopEnvironment.IsVerification;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.SetVirtualHostNameToFolderMapping("studio.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
                string scene = Path.Combine(AppContext.BaseDirectory, "Scene");
                if (Directory.Exists(scene)) core.SetVirtualHostNameToFolderMapping("scene.accessrelay.test", scene, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_, args) => args.Cancel = !TrustedPage(args.Uri);
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += MessageReceivedAsync;
                core.AddWebResourceRequestedFilter(MediaOrigin + "/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += MediaRequestedAsync;
                core.ProcessFailed += InterfaceProcessFailed;
                core.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess) ShowError("The local interface could not open: " + args.WebErrorStatus);
                };
            }
            _ready = false; _web.Navigate(AppUrl); _statusTimer.Start();
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { _initializing = false; }
    }

    private static bool TrustedPage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "studio.local" && uri.IsDefaultPort
        && uri.AbsolutePath is "/index.html" or "/";

    private void InterfaceProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        string diagnostic = $"{DateTimeOffset.Now:O} {args.ProcessFailedKind}: {args.Reason}, exit {args.ExitCode}, {args.ProcessDescription}";
        Trace.TraceError("Desktop interface: {0}", diagnostic);
        if (DesktopEnvironment.DataDirectory("Diagnostics") is { } folder)
        {
            try { Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "interface.log"), diagnostic + Environment.NewLine); }
            catch (Exception) { }
        }
        // GPU and utility processes recover automatically. A browser exit closes the control,
        // so Retry must create a fresh WebView rather than navigate a closed instance.
        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        { _replaceBrowser = true; _web = null; }
        else if (args.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)) return;
        ShowError("The interface stopped. Choose Try opening the interface again to recover, or Stop all processing.");
    }

    private async void MessageReceivedAsync(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closing || !TrustedPage(args.Source)) return;
        string? id = null;
        bool registered = false;
        try
        {
            string json = args.WebMessageAsJson;
            if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("The interface request is too large.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var request = document.RootElement;
            if (request.Number("protocol") != 1) throw new InvalidDataException("The interface protocol is not supported.");
            id = request.Text("id");
            if (id.Length is < 1 or > 128 || id.Any(char.IsControl) || _requests.Count >= 32 || !_requests.Add(id))
                throw new InvalidDataException("The request ID is invalid or already pending.");
            registered = true;
            string method = request.Text("method");
            var parameters = request.TryGetProperty("params", out var supplied) && supplied.ValueKind == JsonValueKind.Object
                ? supplied.Clone() : JsonSerializer.SerializeToElement(new { });
            object? result;
            if (method.StartsWith("controller.", StringComparison.Ordinal)) result = await _controller!.InvokeAsync(method[11..], parameters);
            else if (method.StartsWith("vision.", StringComparison.Ordinal)) result = await _vision!.InvokeAsync(method[7..], parameters);
            else if (method.StartsWith("voice.", StringComparison.Ordinal)) result = await _voice!.InvokeAsync(method[6..], parameters);
            else result = await AppCommandAsync(method, parameters);
            Post(new { type = "response", id, ok = true, result });
            if (method is not ("app.page" or "app.bootstrap" or "app.draftState")) PublishSnapshot();
        }
        catch (Exception error)
        {
            Post(new { type = "response", id, ok = false, error = error is OperationCanceledException ? "The operation was cancelled." : error.Message });
            PublishSnapshot();
        }
        finally { if (registered && id is not null) _requests.Remove(id); }
    }

    private async Task<object?> AppCommandAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "app.bootstrap":
                _ready = true; DesktopLoading.Visibility = Visibility.Collapsed; UpdatePresentation(); return Snapshot();
            case "app.page":
                string page = args.Text("page", "workspace");
                if (page is not ("workspace" or "controller" or "vision" or "voice" or "settings")) throw new InvalidDataException("Choose an available workspace.");
                _page = page; _clientVisible = args.Flag("visible", true); UpdatePresentation(); return null;
            case "app.draftState": _hasUnsavedChanges = args.Flag("dirty"); return null;
            case "app.close":
                if (!args.Flag("discard")) throw new InvalidOperationException("Confirm whether to keep editing before closing.");
                _discardClose = true; DispatcherQueue.TryEnqueue(Close); return null;
            case "app.stopAll": await StopAllAsync(); return new { message = "Capture, voice, scripts and controller output stopped." };
            case "app.installDriver":
                await VirtualMicDriver.InstallAsync(); return new { message = "The virtual device driver is ready." };
            case "app.probeOutput": return new { message = await Task.Run(VirtualOutput.ProbeStatus) };
            case "app.openProfilesFolder":
                Directory.CreateDirectory(_controller!.ProfileDirectory);
                await Windows.System.Launcher.LaunchFolderPathAsync(_controller.ProfileDirectory); return null;
            case "app.openVoiceProfilesFolder":
            {
                string folder = DesktopEnvironment.DataDirectory("VoiceProfiles") ?? new VoiceProfiles().DirectoryPath;
                Directory.CreateDirectory(folder); await Windows.System.Launcher.LaunchFolderPathAsync(folder); return null;
            }
            default: throw new InvalidOperationException("The interface action is not supported: " + method);
        }
    }

    private object Snapshot() => new
    {
        version = BuildInfo.Version, controller = _controller!.GetState(), vision = _vision!.GetState(), voice = _voice!.GetState(),
        settings = new { driverAvailable = VirtualMicDriver.IsAvailable, buildFolder = AppContext.BaseDirectory, appDirectory = AppContext.BaseDirectory,
            profileDirectory = _controller!.ProfileDirectory,
            engine = "C# / .NET", interfaceTechnology = "React / WebView2 · local bundled files", platform = "Windows x64",
            startupError = _startupError, isVerification = DesktopEnvironment.IsVerification },
        system = _performance.Latest
    };

    private void PublishSnapshot()
    {
        if (!_ready || _closing) return;
        try { Post(new { type = "snapshot", state = Snapshot() }); _snapshotAt = Stopwatch.GetTimestamp(); }
        catch (Exception error) { Trace.TraceError("Desktop snapshot: {0}", error.Message); }
    }

    private void PublishStatus()
    {
        if (!_ready || _closing || !IsPresenting()) return;
        try
        {
            Post(new { type = "status", status = new { controller = _controller!.GetStatus(), vision = _vision!.GetStatus(),
                voice = _voice!.GetStatus(), system = _performance.Latest } });
            if (_snapshotAt == 0 || Stopwatch.GetElapsedTime(_snapshotAt).TotalSeconds >= 5) PublishSnapshot();
        }
        catch (Exception error) { Trace.TraceError("Desktop status: {0}", error.Message); }
    }

    private bool IsPresenting() => _clientVisible && AppWindow.IsVisible &&
        (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Minimized);
    private void UpdatePresentation()
    {
        bool visible = !_closing && _ready && IsPresenting();
        _performance.Enabled = visible && _page == "workspace";
        _vision?.SetVisible(visible && _page == "vision"); _voice?.SetVisible(visible && _page == "voice");
    }

    private void Post(object value)
    {
        if (_web is null || _closing) return;
        try { _web.PostWebMessageAsJson(JsonSerializer.Serialize(value, DesktopJson.Options)); }
        catch (Exception error) { Trace.TraceError("Desktop bridge: {0}", error.Message); }
    }

    private async void MediaRequestedAsync(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        try
        {
            var uri = new Uri(args.Request.Uri);
            DesktopMedia? media = null;
            if (!_closing && args.Request.Method == "GET" && uri.Scheme == "https" && uri.Host == "media.accessrelay.test" && uri.IsDefaultPort)
            {
                if (uri.AbsolutePath.StartsWith("/vision/", StringComparison.Ordinal)) media = _vision?.ReadMedia(uri.AbsolutePath[8..]);
                else if (uri.AbsolutePath.StartsWith("/voice/", StringComparison.Ordinal)) media = _voice?.ReadMedia(uri.AbsolutePath[7..]);
            }
            if (media is null)
            { args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store\r\n"); return; }
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            { writer.WriteBytes(media.Bytes); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            args.Response = sender.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + media.ContentType
                + "\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: " + AppOrigin + "\r\nX-Content-Type-Options: nosniff\r\n");
        }
        catch { args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not found", "Cache-Control: no-store\r\n"); }
        finally { deferral.Complete(); }
    }

    private async Task StopAllAsync()
    {
        var failures = new List<Exception>();
        try { _controller?.StopOutput(); } catch (Exception error) { failures.Add(error); }
        try { if (_vision is not null) await _vision.InvokeAsync("stop", JsonSerializer.SerializeToElement(new { })); } catch (Exception error) { failures.Add(error); }
        try { if (_voice is not null) await _voice.InvokeAsync("stop", JsonSerializer.SerializeToElement(new { })); } catch (Exception error) { failures.Add(error); }
        try { _runtime?.SetScriptsEnabled(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Processing stop reported an error.", failures);
    }

    private void ShowError(string message)
    {
        _ready = false; DesktopLoading.Visibility = Visibility.Visible;
        DesktopLoadingText.Text = message; DesktopRetry.Visibility = DesktopEmergencyStop.Visibility = Visibility.Visible; UpdatePresentation();
    }

    private async void ClosingAsync(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        bool unfinishedRecording = _runtime?.Recording is { } recording && (recording.IsRecording || recording.Pending is not null);
        if (_ready && (_hasUnsavedChanges || unfinishedRecording || _vision?.HasActiveTrainingRecording == true) && !_discardClose)
        { Post(new { type = "closeRequested" }); return; }
        _closing = true; _statusTimer.Stop();
        try
        {
            Cleanup(() => _controller?.StopOutput()); Cleanup(() => _controller?.Dispose());
            Cleanup(() => _vision?.Dispose()); Cleanup(() => _voice?.Dispose());
            try { if (_controller is not null) await _controller.WaitForOutputAsync(); } catch (Exception) { }
        }
        finally
        {
            Cleanup(() => _runtime?.Dispose()); _runtime = null; Cleanup(() => _performance.Dispose());
            _allowClose = true; Close();
        }
    }

    private static void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception error) { Trace.TraceError("Desktop shutdown cleanup: {0}", error.Message); }
    }
}
