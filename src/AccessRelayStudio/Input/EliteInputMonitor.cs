using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using Windows.Gaming.Input;

namespace EliteScriptStudio.Input;

public sealed record ControllerDevice(
    string Id, string Name, ushort VendorId, ushort ProductId, string Transport,
    bool IsElite, bool SupportsEliteKeypad);

public readonly record struct ControllerSample(
    bool IsConnected,
    ControllerState State,
    string Source,
    string DeviceId,
    bool IsPaddleInterfacePresent,
    string PaddleSource,
    PaddleButtons HeldPaddles,
    PaddleButtons PressedPaddles,
    long ConnectionVersion)
{
    public ControllerDevice? Device { get; init; }
    public IReadOnlyList<ControllerDevice> Devices { get; init; } = Array.Empty<ControllerDevice>();
}

/// <summary>Reads standardized physical Windows gamepads and the optional wired Elite keypad.</summary>
/// <remarks>Construct and dispose on the HWND thread. Poll from one engine thread.</remarks>
public sealed class EliteInputMonitor : IDisposable
{
    private const ushort MicrosoftVendor = 0x045E;
    private const ushort EliteSeries2Usb = 0x0B00;
    private const ushort VirtualXbox360 = 0x028E;
    private readonly RawPaddleInput _paddles;
    private Gamepad? _gamepad;
    private ControllerDevice? _device;
    private IReadOnlyList<ControllerDevice> _devices = Array.Empty<ControllerDevice>();
    private string? _selectedId;
    private string _source = "Searching for physical controllers";
    private string[] _lastIds = [];
    private GamepadInventory _inventory = GamepadInventory.Empty;
    private long _nextScan;
    private long _connectionVersion;
    private long _paddleVersion;
    private int _scanRequested = 1;
    private int _disposed;
    private int _outputActive;

    public EliteInputMonitor(nint hwnd)
    {
        _paddles = new RawPaddleInput(hwnd);
        RawGameController.RawGameControllerAdded += OnControllerChanged;
        RawGameController.RawGameControllerRemoved += OnControllerChanged;
    }

    public void SelectDevice(string? id)
    {
        Volatile.Write(ref _selectedId, string.IsNullOrWhiteSpace(id) ? null : id);
        Interlocked.Exchange(ref _scanRequested, 1);
    }

    // WGI cannot disambiguate a wired Xbox 360 from Studio's matching virtual
    // identity. Reserve that identity before creating an output device.
    public void SetVirtualOutputActive(bool active)
    {
        if (Interlocked.Exchange(ref _outputActive, active ? 1 : 0) != (active ? 1 : 0))
            Interlocked.Exchange(ref _scanRequested, 1);
    }

    public ControllerSample Poll()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EliteInputMonitor));

        long now = Stopwatch.GetTimestamp();
        long previousVersion = _connectionVersion;
        bool scanRequested = Interlocked.Exchange(ref _scanRequested, 0) != 0;
        if (scanRequested || now >= _nextScan)
        {
            _nextScan = now + Stopwatch.Frequency / 4;
            ScanControllers(scanRequested);
        }

        ControllerState state = default;
        if (_gamepad is not null)
        {
            try
            {
                state = ConvertReading(_gamepad.GetCurrentReading());
            }
            catch (COMException)
            {
                SetController(null, null, "Physical controller disconnected");
                Interlocked.Exchange(ref _scanRequested, 1);
            }
        }

        if (_device is { } selected && IsReserved(new(selected.VendorId, selected.ProductId)))
        {
            SetController(null, null, "Xbox 360 input is ambiguous while virtual output is active");
            state = default;
        }

        var paddles = _paddles.Sample();
        bool supportsKeypad = _device is { SupportsEliteKeypad: true };
        if (paddles.Version != _paddleVersion)
        {
            _paddleVersion = paddles.Version;
            if (supportsKeypad)
                _connectionVersion++;
        }

        bool connected = _gamepad is not null;
        return new(
            connected,
            state,
            _source,
            _device?.Id ?? string.Empty,
            supportsKeypad && paddles.IsPresent,
            supportsKeypad ? paddles.Source : "The Xbox Accessories keypad is available for a single wired Elite Series 2",
            connected && supportsKeypad ? paddles.Held : PaddleButtons.None,
            connected && supportsKeypad && previousVersion == _connectionVersion ? paddles.Pressed : PaddleButtons.None,
            _connectionVersion)
        {
            Device = _device,
            Devices = _devices
        };
    }

    private void OnControllerChanged(object? sender, RawGameController controller) =>
        Interlocked.Exchange(ref _scanRequested, 1);

    private void ScanControllers(bool refreshTopology)
    {
        try
        {
            var rawControllers = RawGameController.RawGameControllers.ToArray();
            var windowsGamepads = rawControllers.Select(ToCandidate)
                .OfType<GamepadCandidate>().ToArray();
            var ids = windowsGamepads.Select(device => device.Id).Order(StringComparer.Ordinal).ToArray();
            if (refreshTopology || !ids.SequenceEqual(_lastIds))
            {
                _lastIds = ids;
                _inventory = PhysicalGamepadInventory.Find(windowsGamepads.Select(device => device.Hardware).ToHashSet());
            }

            var verified = new List<(ControllerDevice Device, Gamepad Gamepad)>();
            foreach (var group in windowsGamepads.GroupBy(device => device.Hardware))
            {
                var physical = _inventory.Physical.Where(device => device.Hardware == group.Key).ToArray();
                if (!CanSelectIdentity(group.Key, group.Count(), physical, _inventory.Virtual, Volatile.Read(ref _outputActive) != 0))
                    continue;

                string[] transports = physical.Select(device => device.Transport).Distinct().ToArray();
                bool elite = group.Key.Vendor == MicrosoftVendor && group.Key.Product is EliteSeries2Usb or 0x0B05 or 0x0B22;
                foreach (var candidate in group)
                {
                    string transport = transports.Length == 1 ? transports[0] : "USB or Bluetooth";
                    if (transport == "USB" && candidate.IsWireless)
                        transport = "Wireless adapter";
                    bool keypad = elite && group.Key.Product == EliteSeries2Usb && transport == "USB" && physical.Length == 1;
                    verified.Add((new(candidate.Id, candidate.Name, group.Key.Vendor, group.Key.Product, transport, elite, keypad),
                        candidate.Gamepad));
                }
            }

            verified = verified.OrderByDescending(candidate => candidate.Device.SupportsEliteKeypad)
                .ThenBy(candidate => candidate.Device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate.Device.Id, StringComparer.Ordinal).ToList();
            var descriptors = verified.Select(candidate => candidate.Device).ToArray();
            if (!_devices.SequenceEqual(descriptors))
                _devices = Array.AsReadOnly(descriptors);

            string? selectedId = Volatile.Read(ref _selectedId);
            var selected = selectedId is null
                ? verified.FirstOrDefault(candidate => candidate.Device.Id == _device?.Id)
                : verified.FirstOrDefault(candidate => candidate.Device.Id == selectedId);
            if (selected.Device is null && selectedId is null)
                selected = verified.FirstOrDefault();
            if (selected.Device is { } device)
                SetController(selected.Gamepad, device, $"{device.Name} · {device.Transport} · {device.VendorId:X4}:{device.ProductId:X4}");
            else
                SetController(null, null, selectedId is not null ? "Selected controller is disconnected or unavailable" :
                    windowsGamepads.Length > 0 ? "Windows reported a gamepad with unverified or virtual hardware identity" :
                    rawControllers.Length > 0 ? "Windows reported a controller without a standard gamepad layout" :
                    "Connect a Windows-compatible gamepad by USB or Bluetooth");
        }
        catch (COMException)
        {
            _devices = Array.Empty<ControllerDevice>();
            SetController(null, null, "Windows controller enumeration is temporarily unavailable");
        }
    }

    private bool IsReserved(HardwareId hardware) =>
        IsReserved(hardware, Volatile.Read(ref _outputActive) != 0);

    private static bool IsReserved(HardwareId hardware, bool outputActive) =>
        outputActive && hardware is { Vendor: MicrosoftVendor, Product: VirtualXbox360 };

    internal static bool CanSelectIdentity(HardwareId hardware, int candidateCount, PhysicalGamepad[] physical,
        IReadOnlySet<HardwareId> virtualIds, bool outputActive) =>
        candidateCount > 0 && !IsReserved(hardware, outputActive) && !virtualIds.Contains(hardware) &&
        candidateCount <= physical.Select(device => device.Container).Distinct().Count();

    private static GamepadCandidate? ToCandidate(RawGameController raw)
    {
        var gamepad = Gamepad.FromGameController(raw);
        string id = raw.NonRoamableId.TrimEnd('\0');
        if (gamepad is null || id.Length == 0)
            return null;
        string name = raw.DisplayName;
        return new(id, string.IsNullOrWhiteSpace(name) ? "Windows gamepad" : name,
            new(raw.HardwareVendorId, raw.HardwareProductId), raw.IsWireless, gamepad);
    }

    private void SetController(Gamepad? controller, ControllerDevice? device, string source)
    {
        if (device != _device)
            _connectionVersion++;
        _gamepad = controller;
        _device = device;
        _source = source;
    }

    private sealed record GamepadCandidate(string Id, string Name, HardwareId Hardware, bool IsWireless, Gamepad Gamepad);

    internal static ControllerState ConvertReading(GamepadReading reading)
    {
        ushort buttons = 0;
        void Map(GamepadButtons button, ushort target)
        {
            if ((reading.Buttons & button) != 0)
                buttons |= target;
        }

        Map(GamepadButtons.DPadUp, 0x0001);
        Map(GamepadButtons.DPadDown, 0x0002);
        Map(GamepadButtons.DPadLeft, 0x0004);
        Map(GamepadButtons.DPadRight, 0x0008);
        Map(GamepadButtons.Menu, 0x0010);
        Map(GamepadButtons.View, 0x0020);
        Map(GamepadButtons.LeftThumbstick, 0x0040);
        Map(GamepadButtons.RightThumbstick, 0x0080);
        Map(GamepadButtons.LeftShoulder, 0x0100);
        Map(GamepadButtons.RightShoulder, 0x0200);
        Map(GamepadButtons.A, 0x1000);
        Map(GamepadButtons.B, 0x2000);
        Map(GamepadButtons.X, 0x4000);
        Map(GamepadButtons.Y, 0x8000);

        return new(
            buttons, Trigger(reading.LeftTrigger), Trigger(reading.RightTrigger),
            Axis(reading.LeftThumbstickX), Axis(reading.LeftThumbstickY),
            Axis(reading.RightThumbstickX), Axis(reading.RightThumbstickY));
    }

    private static byte Trigger(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static short Axis(double value) =>
        (short)Math.Round(Math.Clamp(value, -1, 1) * (value < 0 ? 32768 : 32767));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        RawGameController.RawGameControllerAdded -= OnControllerChanged;
        RawGameController.RawGameControllerRemoved -= OnControllerChanged;
        _paddles.Dispose();
    }
}
