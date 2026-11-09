using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace EliteScriptStudio;

public sealed partial class ControllerScene : UserControl
{
    private const string HostName = "studio.local";
    private const string SceneUrl = "https://studio.local/Viewer/index.html";
    private const string Auto = "auto";
    private const string Xbox = "xbox-one";
    private const string Elite = "elite-series-2";
    private const string EliteSchematic = "elite-schematic";
    private static readonly ModelChoice[] Models =
    [
        new(Xbox, "Xbox One / Series style", "XboxOne.glb"),
        new(Elite, "Xbox Elite Series 2", "EliteSeries2Articulated.glb"),
        new("dualsense", "DualSense (DualShock 4 illustration)", "DualShock4.glb"),
        new("dualshock-4", "PlayStation DualShock 4", "DualShock4.glb"),
        new("switch-pro", "Nintendo Switch Pro", "SwitchPro.glb"),
        new(EliteSchematic, "Xbox Elite Series 2 schematic")
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DispatcherTimer _loadTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly Dictionary<string, string> _bindingNames = new();
    private SceneState _state = new(default, 0, "LargeRight", 0, false);
    private SceneState? _sent;
    private bool _ready;
    private bool _failed;
    private bool _closed;
    private readonly string _sceneDirectory = Path.Combine(AppContext.BaseDirectory, "Scene");
    private readonly IReadOnlyList<ModelChoice> _available;
    private string _deviceModel = Xbox;
    private string _layout = Xbox;
    private string? _page;
    private string? _viewerUnavailable;
    private CoreWebView2? _web;

    public event EventHandler<string>? ControlSelected;
    public event EventHandler<bool>? EliteLayoutChanged;

    public ControllerScene()
    {
        _available = Models.Where(model => model.File is null || File.Exists(Path.Combine(_sceneDirectory, "Model", model.File))).ToArray();
        InitializeComponent();
        ModelPicker.DisplayMemberPath = nameof(ModelChoice.Label);
        ModelPicker.ItemsSource = new[] { new ModelChoice(Auto, "Auto-detect") }.Concat(_available).ToArray();
        ModelPicker.SelectedIndex = 0;
        Viewport.DefaultBackgroundColor = ColorHelper.FromArgb(255, 18, 25, 34);
        _loadTimer.Tick += (_, _) => ShowFallback("The 3D layout didn’t finish loading.");
        Loaded += OpenScene;
    }

    public void Select(string control)
    {
        _state = _state with { Selected = control };
        if (SchematicMap.Visibility == Visibility.Visible)
            SchematicMap.Select(control);
        PublishState();
    }

    public void UpdateBindingHints(Func<ControlId, string> describe, Func<ControlId, bool>? isAssigned = null)
    {
        uint assigned = 0;
        _bindingNames.Clear();
        foreach (var control in ControlMap.All)
        {
            _bindingNames.Add(control.ToString(), describe(control));
            if (isAssigned?.Invoke(control) == true)
                assigned |= (uint)control;
        }
        _state = _state with { Assigned = assigned };
        SchematicMap.UpdateBindingHints(describe, isAssigned);
        PublishBindings();
        PublishState();
    }

    public void ShowInput(ControllerState state, uint controls, bool connected)
    {
        _state = _state with { Input = state, Controls = controls, Connected = connected };
        if (SchematicMap.Visibility == Visibility.Visible)
            SchematicMap.ShowInput(state, controls);
        PublishState();
    }

    public void UseDevice(ControllerDevice? device)
    {
        string model = device switch
        {
            { IsElite: true } => Elite,
            { VendorId: 0x054c, ProductId: 0x0ce6 or 0x0df2 } => "dualsense",
            { VendorId: 0x054c, ProductId: 0x05c4 or 0x09cc or 0x0ba0 } => "dualshock-4",
            { VendorId: 0x057e, ProductId: 0x2009 } => "switch-pro",
            _ => Xbox
        };
        _deviceModel = _available.Any(choice => choice.Key == model) ? model : model == Elite ? EliteSchematic : Xbox;
        UpdateModel();
    }

    private void ModelChanged(object sender, SelectionChangedEventArgs args) => UpdateModel();

    private void UpdateModel()
    {
        string target = (ModelPicker.SelectedItem as ModelChoice)?.Key ?? Auto;
        if (target == Auto)
            target = _deviceModel;
        if (_layout == target)
            return;
        bool wasElite = IsEliteLayout(_layout);
        _layout = target;
        NavigateModel();
        bool elite = IsEliteLayout(_layout);
        if (elite != wasElite)
            EliteLayoutChanged?.Invoke(this, elite);
    }

    private static bool IsEliteLayout(string model) => model is Elite or EliteSchematic;

    private void NavigateModel()
    {
        _loadTimer.Stop();
        _page = null;
        _ready = false;
        _failed = _viewerUnavailable is not null;
        _sent = null;
        SceneStatus.Text = _viewerUnavailable ?? "Loading the 3D control layout…";
        UpdateSceneLayout();
        if (_closed || _failed || _layout == EliteSchematic || _web is null)
            return;
        _page = $"{SceneUrl}?model={_layout}";
        _loadTimer.Start();
        _web.Navigate(_page);
    }

    private void UpdateSceneLayout()
    {
        bool schematic = _layout == EliteSchematic || _failed;
        bool scene = _ready && !schematic;
        SchematicMap.Visibility = schematic ? Visibility.Visible : Visibility.Collapsed;
        if (schematic)
        {
            SchematicMap.ShowPaddles(IsEliteLayout(_layout));
            SchematicMap.Select(_state.Selected);
            SchematicMap.ShowInput(_state.Input, _state.Controls);
        }
        Viewport.Opacity = scene ? 1 : 0;
        Viewport.IsHitTestVisible = scene;
        Viewport.IsTabStop = scene;
        SceneNotice.Visibility = !_ready && _layout != EliteSchematic ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Close()
    {
        _closed = true;
        _ready = false;
        _loadTimer.Stop();
        Viewport.Close();
    }

    private async void OpenScene(object sender, RoutedEventArgs args)
    {
        Loaded -= OpenScene;
        if (!File.Exists(Path.Combine(_sceneDirectory, "Viewer", "index.html")))
        {
            ShowFallback("The 3D layout files are missing.", unavailable: true);
            return;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            string level = principal.IsInRole(WindowsBuiltInRole.Administrator) ? "Elevated" : "Desktop";
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EliteScriptStudio", "SceneCache", level);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, data, new CoreWebView2EnvironmentOptions());
            if (_closed || _failed)
                return;
            await Viewport.EnsureCoreWebView2Async(environment);
            if (_closed || _failed)
                return;
            var core = Viewport.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.SetVirtualHostNameToFolderMapping(HostName, _sceneDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, navigation) => navigation.Cancel = !IsScenePage(navigation.Uri);
            core.NewWindowRequested += (_, popup) => popup.Handled = true;
            core.DownloadStarting += (_, download) => download.Cancel = true;
            core.NavigationCompleted += (_, navigation) =>
            {
                if (!navigation.IsSuccess && navigation.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowFallback("The 3D viewer couldn’t open.");
            };
            core.ProcessFailed += (_, _) => ShowFallback("The 3D viewer stopped.");
            core.WebMessageReceived += SceneMessageReceived;
            _web = core;
            NavigateModel();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            ShowFallback("WebView2 couldn’t start the 3D viewer.", unavailable: true);
        }
    }

    private void SceneMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closed || _failed || !IsScenePage(args.Source))
            return;
        string json = args.WebMessageAsJson;
        if (json.Length > 2048)
            return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return;

            switch (type.GetString())
            {
                case "ready" when message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                    && model.GetString() == _layout:
                    _loadTimer.Stop();
                    _ready = true;
                    _sent = null;
                    UpdateSceneLayout();
                    PublishBindings();
                    PublishState();
                    break;
                case "select" when _ready && message.TryGetProperty("control", out var control) && control.ValueKind == JsonValueKind.String:
                    string? name = control.GetString();
                    if (Enum.TryParse<ControlId>(name, out var id) && name == id.ToString() && ControlMap.All.Contains(id))
                        ControlSelected?.Invoke(this, name);
                    break;
                case "error":
                    if (message.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String)
                        System.Diagnostics.Debug.WriteLine(detail.GetString());
                    ShowFallback("The 3D layout couldn’t render.");
                    break;
            }
        }
        catch (JsonException) { }
    }

    private void SchematicSelected(object? sender, string control) => ControlSelected?.Invoke(this, control);

    private void ShowFallback(string message, bool unavailable = false)
    {
        if (_closed || _failed)
            return;
        _failed = true;
        _ready = false;
        _loadTimer.Stop();
        SceneStatus.Text = $"{message} Showing the interactive schematic.";
        if (unavailable)
            _viewerUnavailable = SceneStatus.Text;
        UpdateSceneLayout();
    }

    private void PublishBindings()
    {
        if (_ready)
            Post(new { type = "bindings", bindings = _bindingNames });
    }

    private void PublishState()
    {
        if (!_ready || _state == _sent)
            return;
        var input = _state.Input;
        if (Post(new
        {
            type = "state", input.LeftX, input.LeftY, input.RightX, input.RightY,
            input.LeftTrigger, input.RightTrigger, _state.Controls, _state.Selected, _state.Assigned, _state.Connected
        }))
            _sent = _state;
    }

    private bool Post<T>(T message)
    {
        try
        {
            Viewport.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(error);
            ShowFallback("The 3D viewer disconnected.");
            return false;
        }
    }

    private bool IsScenePage(string source) => _page is not null && string.Equals(source, _page, StringComparison.Ordinal);

    private sealed record ModelChoice(string Key, string Label, string? File = null);
    private readonly record struct SceneState(ControllerState Input, uint Controls, string Selected, uint Assigned, bool Connected);
}
