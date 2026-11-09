using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Input;

internal readonly record struct PaddleSample(
    bool IsPresent, string Source, PaddleButtons Held, PaddleButtons Pressed, long Version);

/// <summary>Listens for the four PC Profile keypad mappings on the controller's own USB HID keyboard.</summary>
internal sealed class RawPaddleInput : IDisposable
{
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint WmNcDestroy = 0x0082;
    private const uint DeviceRemoval = 2;
    private const uint InputSink = 0x00000100;
    private const uint DeviceNotify = 0x00002000;
    private const uint RemoveRegistration = 0x00000001;
    private const uint KeyboardType = 1;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const ushort KeyBreak = 1;
    private const uint NativeError = uint.MaxValue;
    private const string ControllerKeyboardPrefix = @"\\?\HID#VID_045E&PID_0B02&IG_00#";
    private const string MissingKeyboard = "Connect the Xbox Accessories PC Profile USB keyboard interface (045E:0B02)";

    private static readonly uint DeviceSize = (uint)Marshal.SizeOf<RawInputDevice>();
    private static readonly uint EntrySize = (uint)Marshal.SizeOf<RawInputDeviceList>();
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RawInputHeader>();
    private static readonly uint KeyboardMinimumSize = HeaderSize + (uint)Marshal.SizeOf<RawKeyboard>();
    private static readonly uint PacketCapacity = (uint)Marshal.SizeOf<RawInputPacket>();

    private readonly object _gate = new();
    private readonly nint _hwnd;
    private readonly WindowSubclass _subclass;
    private nint _keyboard;
    private string _source = MissingKeyboard;
    private PaddleButtons _held;
    private PaddleButtons _pressed;
    private long _version;
    private bool _hooked;
    private bool _registered;

    public RawPaddleInput(nint hwnd)
    {
        _hwnd = hwnd;
        _subclass = HandleWindowMessage;
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out _) != GetCurrentThreadId())
            throw new ArgumentException("Attach paddle input on the owner thread of a live window.", nameof(hwnd));

        _hooked = SetWindowSubclass(hwnd, _subclass, 1, 0);
        if (!_hooked)
        {
            _source = "Windows refused the paddle input window hook";
            return;
        }

        _registered = RegisterRawInputDevices([KeyboardRegistration(hwnd, InputSink | DeviceNotify)], 1, DeviceSize);
        if (!_registered)
        {
            _source = $"Windows refused the paddle keyboard registration ({new Win32Exception().Message})";
            RemoveWindowSubclass(hwnd, _subclass, 1);
            _hooked = false;
            return;
        }

        FindKeyboard();
    }

    public PaddleSample Sample()
    {
        lock (_gate)
        {
            var result = new PaddleSample(_keyboard != 0, _source, _held, _pressed, _version);
            _pressed = PaddleButtons.None;
            return result;
        }
    }

    private nint HandleWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint data)
    {
        if (message == WmInput)
            HandleKeyboard(lParam);
        else if (message == WmInputDeviceChange)
            FindKeyboard(wParam == DeviceRemoval ? lParam : 0);
        else if (message == WmNcDestroy)
            Dispose();

        // Let the default window procedure perform raw input cleanup, including foreground WM_INPUT.
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void HandleKeyboard(nint packet)
    {
        uint size = PacketCapacity;
        uint copied = GetRawInputData(packet, RidInput, out var input, ref size, HeaderSize);
        if (copied < KeyboardMinimumSize || copied > PacketCapacity || input.Header.Type != KeyboardType)
            return;

        ProcessKeyboardEvent(input.Header.Device, input.Payload.Keyboard.VirtualKey, input.Payload.Keyboard.Flags);
    }

    internal void ProcessKeyboardEvent(nint device, ushort virtualKey, ushort flags)
    {
        var button = MappedKey(virtualKey);
        if (button == PaddleButtons.None)
            return;

        lock (_gate)
        {
            if (_keyboard == 0 || device != _keyboard)
                return;
            if ((flags & KeyBreak) != 0)
            {
                _held &= ~button;
            }
            else if ((_held & button) == 0)
            {
                _held |= button;
                _pressed |= button;
            }
        }
    }

    private void FindKeyboard(nint removed = 0)
    {
        nint keyboard = 0;
        string source = MissingKeyboard;
        foreach (var device in ReadDeviceList())
        {
            if (device.Type != KeyboardType || device.Handle == removed)
                continue;
            string? name = ReadDeviceName(device.Handle);
            if (name is null || !IsControllerKeyboard(name))
                continue;

            if (keyboard != 0)
            {
                keyboard = 0;
                source = "Multiple Elite USB keyboard interfaces found";
                break;
            }

            keyboard = device.Handle;
            source = name;
        }

        SetKeyboard(keyboard, source);
    }

    private void SetKeyboard(nint keyboard, string source)
    {
        lock (_gate)
        {
            if (_keyboard != keyboard || _source != source)
            {
                _held = PaddleButtons.None;
                _pressed = PaddleButtons.None;
                _version++;
            }
            _keyboard = keyboard;
            _source = source;
        }
    }

    internal static bool IsControllerKeyboard(string name) =>
        name.StartsWith(ControllerKeyboardPrefix, StringComparison.OrdinalIgnoreCase);

    internal static PaddleButtons MappedKey(ushort key) => key switch
    {
        0x6F => PaddleButtons.SmallLeft,  // Numpad /
        0x6A => PaddleButtons.SmallRight, // Numpad *
        0x6D => PaddleButtons.LargeLeft,  // Numpad -
        0x6B => PaddleButtons.LargeRight, // Numpad +
        _ => PaddleButtons.None
    };

    internal static RawInputDeviceList[] ReadDeviceList()
    {
        // The device count can change between the two Win32 calls.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, EntrySize) == NativeError || count == 0)
                return [];
            var devices = new RawInputDeviceList[count];
            uint written = GetRawInputDeviceList(devices, ref count, EntrySize);
            if (written != NativeError)
                return devices[..(int)written];
        }
        return [];
    }

    internal static string? ReadDeviceName(nint device)
    {
        uint length = 0;
        if (GetRawInputDeviceInfoSize(device, RidiDeviceName, 0, ref length) == NativeError || length == 0)
            return null;
        var name = new StringBuilder(checked((int)length));
        return GetRawInputDeviceInfoName(device, RidiDeviceName, name, ref length) == NativeError ? null : name.ToString();
    }

    private static RawInputDevice KeyboardRegistration(nint hwnd, uint flags) => new()
    {
        UsagePage = 1,
        Usage = 6,
        Flags = flags,
        Target = hwnd
    };

    public void Dispose()
    {
        if (_registered)
        {
            RegisterRawInputDevices([KeyboardRegistration(0, RemoveRegistration)], 1, DeviceSize);
            _registered = false;
        }
        if (_hooked)
        {
            RemoveWindowSubclass(_hwnd, _subclass, 1);
            _hooked = false;
        }
        SetKeyboard(0, "Paddle input stopped");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceList
    {
        public nint Handle;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    // The native union can be larger than a keyboard reading.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawInputPayload
    {
        [FieldOffset(0)] public RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputPacket
    {
        public RawInputHeader Header;
        public RawInputPayload Payload;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoSize(nint device, uint command, nint data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoName(nint device, uint command, [Out] StringBuilder data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint packet, uint command, out RawInputPacket data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
