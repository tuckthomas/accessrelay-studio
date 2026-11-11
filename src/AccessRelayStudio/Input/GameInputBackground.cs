using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace EliteScriptStudio.Input;

/// <summary>Reads a uniquely matched gamepad through the Windows GameInput runtime.</summary>
/// <remarks>Bind and read on the input polling thread. Dispose after polling has stopped.</remarks>
internal sealed class GameInputBackground : IDisposable
{
    private const uint GamepadKind = 0x00040000;
    private const uint Connected = 1;
    private const uint BlockingEnumeration = 2;
    private const uint DefaultFocusPolicy = 0;
    private const ulong CallbackWaitUs = 1_000_000;
    private static readonly Guid GameInputV0 = new("11BE2A7E-4254-445A-9C09-FFC40F006918");
    private static readonly Guid GameInputV4 = new("F2DAAE4C-4D21-4250-8953-4F791E6279AB");

    private readonly CurrentReading _read;
    private readonly RegisterDeviceCallback? _register;
    private readonly StopCallback? _stop;
    private readonly UnregisterCallback? _unregister;
    private readonly RedistDevices? _redistDevices;
    private RedistDevice? _redistDevice;
    private nint _input;
    private nint _device;
    private DeviceStatus? _deviceStatus;
    private nint _readingVtable;
    private GamepadState? _gamepadState;
    private ReadingTimestamp? _timestamp;
    private DeviceCallback? _stoppedCallback;
    private ulong _stoppedToken;

    private GameInputBackground(nint input, RedistDevices? redistDevices = null)
    {
        _input = input;
        _read = Method<CurrentReading>(input, 4);
        _redistDevices = redistDevices;
        if (redistDevices is not null)
            return;

        // Inbox v0 slots are from the Windows 10.0.26100.0 SDK GameInput.h.
        _register = Method<RegisterDeviceCallback>(input, 9);
        _stop = Method<StopCallback>(input, 12);
        _unregister = Method<UnregisterCallback>(input, 13);
        Method<FocusPolicy>(input, 21)(input, DefaultFocusPolicy);
    }

    public static GameInputBackground? TryCreate() => TryCreateRedist() ?? TryCreateInbox();

    private static GameInputBackground? TryCreateRedist()
    {
        nint input = 0;
        RedistDevices? devices = null;
        try
        {
            if (GameInputInitialize(in GameInputV4, out input) < 0 || input == 0)
                return null;

            // The v4 API requires explicitly opting in to background controller readings.
            Method<EnableBackgroundInput>(input, 13)(input, true);
            devices = new RedistDevices(input);
            if (!devices.Start())
                return null;

            var background = new GameInputBackground(input, devices);
            devices = null;
            input = 0;
            return background;
        }
        catch (Exception error) when (IsUnavailable(error)) { return null; }
        finally
        {
            devices?.Dispose();
            Release(input);
        }
    }

    private static GameInputBackground? TryCreateInbox()
    {
        nint created = 0;
        nint input = 0;
        try
        {
            if (GameInputCreate(out created) < 0 || created == 0 ||
                Marshal.QueryInterface(created, in GameInputV0, out input) < 0 || input == 0)
                return null;

            var background = new GameInputBackground(input);
            input = 0; // Ownership transferred to the wrapper.
            return background;
        }
        catch (Exception error) when (IsUnavailable(error)) { return null; }
        finally
        {
            Release(input);
            Release(created);
        }
    }

    private static bool IsUnavailable(Exception error) => error is DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException or COMException;

    public bool IsBound => _device != 0 && (_redistDevices is null || _redistDevices.IsSelected(_redistDevice!));

    public void Bind(HardwareId? hardware)
    {
        Release(_device);
        _device = 0;
        _deviceStatus = null;
        _redistDevice = null;
        if (hardware is not { } identity || _input == 0 || _stoppedCallback is not null)
            return;

        if (_redistDevices is not null)
        {
            _redistDevice = _redistDevices.AcquireUnique(identity);
            _device = _redistDevice?.Pointer ?? 0;
            return;
        }

        var match = new DeviceMatch(identity);
        DeviceCallback callback = match.Observe;
        bool registered = _register!(_input, 0, GamepadKind, Connected, BlockingEnumeration, 0, callback, out ulong token) >= 0;
        bool complete = registered && _unregister!(_input, token, CallbackWaitUs);
        if (registered && !complete)
        {
            _stop!(_input, token);
            // Keep the callback rooted while native code may still be returning from it.
            _stoppedCallback = callback;
            _stoppedToken = token;
        }
        GC.KeepAlive(callback);
        _device = match.Finish(complete);
        if (_device != 0)
            _deviceStatus = Method<DeviceStatus>(_device, 4);
    }

    public bool TryRead(out GamepadReading reading)
    {
        reading = default;
        if (_input == 0)
            return false;

        if (!IsBound || (_deviceStatus is not null && (_deviceStatus(_device) & Connected) == 0))
            return false;

        nint sample = 0;
        try
        {
            if (_read(_input, GamepadKind, _device, out sample) < 0 || sample == 0)
                return false;

            nint vtable = Marshal.ReadIntPtr(sample);
            if (vtable != _readingVtable)
            {
                _timestamp = Method<ReadingTimestamp>(sample, _redistDevices is not null ? 4 : 5);
                _gamepadState = Method<GamepadState>(sample, _redistDevices is not null ? 18 : 22);
                _readingVtable = vtable;
            }
            if (!_gamepadState!(sample, out var state))
                return false;

            reading = new GamepadReading
            {
                Timestamp = _timestamp!(sample), Buttons = (GamepadButtons)state.Buttons,
                LeftTrigger = state.LeftTrigger, RightTrigger = state.RightTrigger,
                LeftThumbstickX = state.LeftX, LeftThumbstickY = state.LeftY,
                RightThumbstickX = state.RightX, RightThumbstickY = state.RightY
            };
            return true;
        }
        finally { Release(sample); }
    }

    public void Dispose()
    {
        Bind(null);
        if (_input == 0)
            return;
        _redistDevices?.Dispose();
        if (_stoppedCallback is not null)
            _unregister!(_input, _stoppedToken, CallbackWaitUs);
        Release(_input);
        _input = 0;
        GC.KeepAlive(_stoppedCallback);
        _stoppedCallback = null;
    }

    // v4 reports connected devices asynchronously when registering and on topology changes.
    // Its slots are from the Microsoft.GameInput 4.0.306 v4/GameInput.h.
    private sealed record RedistDevice(nint Pointer, HardwareId Hardware, IsDeviceConnected IsConnected);

    private sealed class RedistDevices : IDisposable
    {
        private const uint DeviceConnected = 0;
        private const uint DeviceDisconnected = 1;
        private readonly nint _input;
        private readonly object _gate = new();
        private readonly RedistDeviceCallback _callback;
        private readonly Dictionary<nint, RedistDevice> _devices = [];
        private ulong _token;
        private bool _disposed;

        public RedistDevices(nint input)
        {
            _input = input;
            _callback = Observe;
        }

        public bool Start() => Method<RedistRegisterDeviceCallback>(_input, 8)(
            _input, 0, GamepadKind, 0, _callback, out _token) >= 0;

        public RedistDevice? AcquireUnique(HardwareId hardware)
        {
            lock (_gate)
            {
                var match = FindUnique(hardware);
                if (match is not null)
                    Marshal.AddRef(match.Pointer);
                return match;
            }
        }

        public bool IsSelected(RedistDevice selected)
        {
            lock (_gate)
                return ReferenceEquals(FindUnique(selected.Hardware), selected);
        }

        private RedistDevice? FindUnique(HardwareId hardware)
        {
            RedistDevice? match = null;
            foreach (var entry in _devices.Values)
            {
                if (entry.Hardware != hardware || !entry.IsConnected(entry.Pointer))
                    continue;
                if (match is not null)
                    return null;
                match = entry;
            }
            return match;
        }

        private void Observe(ulong token, nint context, uint kind, nint device)
        {
            if (device == 0)
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;
                if (kind == DeviceDisconnected)
                {
                    if (_devices.Remove(device))
                        Release(device);
                    return;
                }
                if (kind != DeviceConnected || _devices.ContainsKey(device))
                    return;

                try
                {
                    var hardware = new HardwareId(Method<DeviceIdentifier>(device, 3)(device),
                        Method<DeviceIdentifier>(device, 4)(device));
                    var isConnected = Method<IsDeviceConnected>(device, 43);
                    _devices.Add(device, new(device, hardware, isConnected));
                    Marshal.AddRef(device);
                }
                catch (Exception)
                {
                    // Keep the input API's unmanaged callback boundary exception-free.
                    _devices.Remove(device);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            if (_token != 0)
                Method<RedistUnregisterCallback>(_input, 9)(_input, _token);
            lock (_gate)
            {
                foreach (nint device in _devices.Keys)
                    Release(device);
                _devices.Clear();
            }
            GC.KeepAlive(_callback);
        }
    }

    private sealed class DeviceMatch(HardwareId hardware)
    {
        private readonly object _gate = new();
        private nint _device;
        private bool _ambiguous;
        private bool _finished;

        public void Observe(ulong token, nint context, nint device, ulong timestamp, uint status, uint previous)
        {
            if (device == 0)
                return;

            lock (_gate)
            {
                if (_finished || _ambiguous)
                    return;

                try
                {
                    if ((status & Connected) == 0)
                    {
                        if (_device == device)
                        {
                            Release(_device);
                            _device = 0;
                        }
                        return;
                    }

                    nint info = Method<DeviceInfo>(device, 3)(device);
                    // GameInputDeviceInfo v0 begins with infoSize, vendorId, productId.
                    if (info == 0 || Marshal.ReadInt32(info) < 8 ||
                        (ushort)Marshal.ReadInt16(info, 4) != hardware.Vendor ||
                        (ushort)Marshal.ReadInt16(info, 6) != hardware.Product || _device == device)
                        return;

                    if (_device != 0)
                        _ambiguous = true;
                    else
                    {
                        Marshal.AddRef(device);
                        _device = device;
                    }
                }
                catch (Exception)
                {
                    // Exceptions cannot propagate across a native callback boundary.
                    _ambiguous = true;
                }
            }
        }

        public nint Finish(bool complete)
        {
            lock (_gate)
            {
                _finished = true;
                nint selected = _device;
                _device = 0;
                if (complete && !_ambiguous)
                    return selected;
                Release(selected);
                return 0;
            }
        }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Release(nint instance)
    {
        if (instance != 0)
            Marshal.Release(instance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGamepadState
    {
        public uint Buttons;
        public float LeftTrigger, RightTrigger, LeftX, LeftY, RightX, RightY;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CurrentReading(nint input, uint kind, nint device, out nint reading);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int RegisterDeviceCallback(nint input, nint device, uint kind, uint status, uint enumeration,
        nint context, DeviceCallback callback, out ulong token);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DeviceCallback(ulong token, nint context, nint device, ulong timestamp, uint status, uint previous);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void StopCallback(nint input, ulong token);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool UnregisterCallback(nint input, ulong token, ulong timeoutUs);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void FocusPolicy(nint input, uint policy);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EnableBackgroundInput(nint input, [MarshalAs(UnmanagedType.U1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int RedistRegisterDeviceCallback(nint input, nint device, uint kind, nint context,
        RedistDeviceCallback callback, out ulong token);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RedistDeviceCallback(ulong token, nint context, uint kind, nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RedistUnregisterCallback(nint input, ulong token);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ushort DeviceIdentifier(nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsDeviceConnected(nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint DeviceInfo(nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceStatus(nint device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong ReadingTimestamp(nint reading);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool GamepadState(nint reading, out NativeGamepadState state);

    [DllImport("GameInput.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GameInputCreate(out nint input);

    [DllImport("GameInputRedist.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GameInputInitialize(in Guid iid, out nint input);
}
