using EliteScriptStudio.Core;

namespace EliteScriptStudio.Input;

/// <summary>Two independently verified physical readers. Configuration/disposal is
/// on the HWND thread; polling owns the readers between configuration transactions.</summary>
internal sealed class CoPilotInputMonitor(EliteInputMonitor primary, Func<EliteInputMonitor> createCompanion) : IControllerInput
{
    private readonly object _gate = new();
    private EliteInputMonitor? _companion;
    private CoPilotSettings _settings = new();
    private long _version = 1, _primaryVersion = -1, _companionVersion = -1;
    private int _reserved;
    private IReadOnlyList<ControllerDevice> _primaryDevices = [], _companionDevices = [], _devices = [];
    private string? _firstSource, _secondSource;
    private string _source = "Co-pilot physical input";
    public CoPilotSettings CoPilotSettings => Volatile.Read(ref _settings);

    public void ConfigureCoPilot(CoPilotSettings settings)
    {
        settings.Validate();
        lock (_gate)
        {
            bool samePair = _settings.Enabled && settings.Enabled && _settings.PrimaryDeviceId == settings.PrimaryDeviceId
                && _settings.CompanionDeviceId == settings.CompanionDeviceId;
            if (settings.Enabled && !samePair && (!_devices.Any(device => device.Id == settings.PrimaryDeviceId)
                || !_devices.Any(device => device.Id == settings.CompanionDeviceId)))
                throw new InvalidOperationException("Choose two available, verified physical controllers.");
            EliteInputMonitor? replacement = null;
            if (settings.Enabled && (_companion is null || _settings.CompanionDeviceId != settings.CompanionDeviceId))
            {
                replacement = createCompanion();
                replacement.SelectDevice(settings.CompanionDeviceId);
                replacement.SetVirtualOutputActive(Volatile.Read(ref _reserved) != 0);
            }
            var retired = !settings.Enabled || replacement is not null ? _companion : null;
            if (replacement is not null) _companion = replacement;
            if (!settings.Enabled) _companion = null;
            if ((settings.Enabled || _settings.Enabled) && settings.PrimaryDeviceId.Length != 0) primary.SelectDevice(settings.PrimaryDeviceId);
            Volatile.Write(ref _settings, settings); _version++;
            // Polling is fenced by _gate; no retired reader can be in a native call.
            retired?.Dispose();
        }
    }
    public ControllerSample Poll()
    {
        lock (_gate)
        {
            primary.SetVirtualOutputActive(Volatile.Read(ref _reserved) != 0);
            var first = primary.Poll();
            var settings = _settings;
            var second = default(ControllerSample);
            if (settings.Enabled && _companion is not null)
            {
                _companion.SetVirtualOutputActive(Volatile.Read(ref _reserved) != 0);
                second = _companion.Poll();
            }
            if (first.ConnectionVersion != _primaryVersion || second.ConnectionVersion != _companionVersion)
            { _primaryVersion = first.ConnectionVersion; _companionVersion = second.ConnectionVersion; _version++; }
            var secondDevices = second.Devices ?? Array.Empty<ControllerDevice>();
            if (!ReferenceEquals(_primaryDevices, first.Devices) || !ReferenceEquals(_companionDevices, secondDevices))
            {
                _primaryDevices = first.Devices; _companionDevices = secondDevices;
                _devices = first.Devices.Concat(_companionDevices).DistinctBy(device => device.Id).ToArray();
            }
            if (!settings.Enabled) return first with { ConnectionVersion = _version, Devices = _devices, PrimaryConnected = first.IsConnected };
            if (_firstSource != first.Source || _secondSource != second.Source)
            { _firstSource = first.Source; _secondSource = second.Source; _source = "Co-pilot · " + first.Source + " + " + second.Source; }
            var merged = CoPilotInput.Merge(first.State, first.IsConnected, second.State, second.IsConnected, settings);
            return first with
            {
                IsConnected = first.IsConnected || second.IsConnected, State = merged, ConnectionVersion = _version,
                Source = _source,
                DeviceId = settings.PrimaryDeviceId, Device = first.Device ?? second.Device, Devices = _devices,
                PrimaryConnected = first.IsConnected, CompanionConnected = second.IsConnected, CompanionDeviceId = settings.CompanionDeviceId,
                NavigationState = first.State, NavigationConnected = first.IsConnected,
                SupportsBackgroundInput = (!first.IsConnected || first.SupportsBackgroundInput) && (!second.IsConnected || second.SupportsBackgroundInput)
            };
        }
    }
    public void SelectDevice(string? id)
    {
        if (CoPilotSettings.Enabled) throw new InvalidOperationException("Turn off co-pilot before changing the primary input selector.");
        primary.SelectDevice(id);
    }
    public void SetVirtualOutputActive(bool active)
    {
        Volatile.Write(ref _reserved, active ? 1 : 0);
        primary.SetVirtualOutputActive(active); Volatile.Read(ref _companion)?.SetVirtualOutputActive(active);
    }
    public Action<double>? CreateVibrationSink(long connectionVersion) =>
        Volatile.Read(ref _version) == connectionVersion ? primary.CreateVibrationSink(Volatile.Read(ref _primaryVersion)) : null;
    public void Dispose() { lock (_gate) { try { _companion?.Dispose(); } finally { primary.Dispose(); } } }
}
