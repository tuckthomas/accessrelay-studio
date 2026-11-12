using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using Windows.Gaming.Input;

const int MessageOnlyWindow = -3;
var hwnd = CreateWindowExW(0, "STATIC", "Elite Script Studio input probe", 0, 0, 0, 0, 0,
    MessageOnlyWindow, 0, 0, 0);
if (hwnd == 0)
    throw new InvalidOperationException($"Could not create the diagnostic message window ({Marshal.GetLastPInvokeError()}).");

try
{
    ControllerSample sample;
    using (var monitor = new EliteInputMonitor(hwnd))
    {
        _ = Task.Run(monitor.Poll).GetAwaiter().GetResult();
        Thread.Sleep(1000);
        sample = Task.Run(monitor.Poll).GetAwaiter().GetResult();
    }
    if (args is ["--inventory-json", var inventoryPath])
    {
        string destination = Path.GetFullPath(inventoryPath);
        if (File.Exists(destination)) throw new IOException("Inventory export never overwrites an existing report.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string? IdentityHash(string id) => id.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        var exportReport = new
        {
            schemaVersion = 1, capturedAtUtc = DateTimeOffset.UtcNow,
            scope = "Read-only physical input discovery; no output, rumble or audio action was requested.",
            hardwareAcceptanceComplete = false, sample.IsConnected, sample.Source, sample.SupportsBackgroundInput,
            sample.IsPaddleInterfacePresent, selectedIdentitySha256 = IdentityHash(sample.DeviceId),
            devices = sample.Devices.Select(device => new { device.Name, device.Transport, device.VendorId, device.ProductId,
                device.IsElite, device.SupportsEliteKeypad, identitySha256 = IdentityHash(device.Id) })
        };
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, exportReport, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
    else if (args.Length != 0) throw new ArgumentException("Usage: EliteInputProbe [--inventory-json new-report.json]");
    Console.WriteLine($"Physical gamepad selected: {sample.IsConnected} · {sample.Source}");
    Console.WriteLine($"Verified selectable gamepads: {sample.Devices.Count}");
    Console.WriteLine($"Controller keyboard found: {sample.IsPaddleInterfacePresent} · {sample.PaddleSource}");
    foreach (var raw in RawGameController.RawGameControllers)
        Console.WriteLine($"WGI: {raw.DisplayName} {raw.HardwareVendorId:X4}:{raw.HardwareProductId:X4}");
    Console.WriteLine($"Raw keyboard devices: {RawPaddleInput.ReadDeviceList().Count(device => device.Type == 1)}");

    var mapped = new ushort[] { 0x6F, 0x6A, 0x6D, 0x6B }.Select(RawPaddleInput.MappedKey);
    if (!mapped.SequenceEqual(new[] { PaddleButtons.SmallLeft, PaddleButtons.SmallRight, PaddleButtons.LargeLeft, PaddleButtons.LargeRight })
        || RawPaddleInput.MappedKey(0x58) != PaddleButtons.None
        || RawPaddleInput.IsControllerKeyboard(@"\\?\HID#HIDMAESTRO_VID_045E_PID_0B02&IG_00#123")
        || RawPaddleInput.IsControllerKeyboard(@"\\?\HID#VID_9999&PID_0B02&IG_00#123"))
        throw new InvalidOperationException("Keyboard source filtering or key mapping failed.");

    var physical = EliteInputMonitor.ConvertReading(new GamepadReading
    {
        Buttons = GamepadButtons.X | GamepadButtons.DPadLeft,
        LeftThumbstickX = -1,
        LeftThumbstickY = 1,
        RightTrigger = 1
    });
    if (physical != new ControllerState(0x4004, 0, 255, short.MinValue, short.MaxValue, 0, 0))
        throw new InvalidOperationException("Windows gamepad conversion lost a passthrough control.");
    Console.WriteLine("PASS controller-keyboard filter, four numpad mappings and physical X/left-stick conversion.");

    var usb = new HardwareId(0x045E, 0x028E);
    var bluetooth = new HardwareId(0x054C, 0x09CC);
    if (!PhysicalGamepadInventory.TryReadHardwareId(@"USB\VID_045E&PID_028E\4&1234", out var usbId) || usbId != usb ||
        !PhysicalGamepadInventory.TryReadHardwareId(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&09CC\8&1234", out var btId) || btId != bluetooth ||
        !PhysicalGamepadInventory.TryReadHardwareId(@"SWD\HIDMAESTRO_VID_045E_PID_028E&IG_00\HM_123", out var virtualId) || virtualId != usb ||
        !PhysicalGamepadInventory.IsVirtualNode(@"SWD\HIDMAESTRO_VID_045E_PID_028E&IG_00\HM_123") ||
        !PhysicalGamepadInventory.IsVirtualNode(@"ROOT\VIGEMBUS\0000") ||
        PhysicalGamepadInventory.ClassifyTransport(false, false, false) is not null ||
        PhysicalGamepadInventory.ClassifyTransport(true, true, false) != "Bluetooth")
        throw new InvalidOperationException("Hardware identity or transport parsing failed.");

    PhysicalGamepad[] physicalGamepads = [new(usb, Guid.NewGuid(), "USB")];
    var none = new HashSet<HardwareId>();
    if (!EliteInputMonitor.CanSelectIdentity(usb, 1, physicalGamepads, none, false) ||
        EliteInputMonitor.CanSelectIdentity(usb, 2, physicalGamepads, none, false) ||
        EliteInputMonitor.CanSelectIdentity(usb, 1, [], none, false) ||
        EliteInputMonitor.CanSelectIdentity(usb, 1, physicalGamepads, none, true) ||
        EliteInputMonitor.CanSelectIdentity(usb, 1, physicalGamepads, new HashSet<HardwareId> { usb }, false) ||
        !EliteInputMonitor.CanSelectIdentity(bluetooth, 1, [new(bluetooth, Guid.NewGuid(), "Bluetooth")], none, true))
        throw new InvalidOperationException("A virtual or ambiguous identity was accepted, or a physical controller was blocked.");
    Console.WriteLine("PASS physical USB/Bluetooth parsing, virtual rejection, ambiguous-source rejection, and Xbox 360 output reservation.");

    var presentUsbIdentities = RawPaddleInput.ReadDeviceList()
        .Select(device => RawPaddleInput.ReadDeviceName(device.Handle))
        .OfType<string>()
        .Select(name => PhysicalGamepadInventory.TryReadHardwareId(name.Replace('#', '\\'), out var hardware) ? hardware : default)
        .Where(hardware => hardware != default)
        .ToHashSet();
    var inventory = PhysicalGamepadInventory.Find(presentUsbIdentities);
    Console.WriteLine($"Read-only Configuration Manager matched {inventory.Physical.Length} physical USB/Bluetooth device identities.");

    if (sample.IsPaddleInterfacePresent)
    {
        using var input = new RawPaddleInput(hwnd);
        var keyboard = RawPaddleInput.ReadDeviceList().Single(device =>
            device.Type == 1 && RawPaddleInput.ReadDeviceName(device.Handle) == sample.PaddleSource);
        input.ProcessKeyboardEvent(0, 0x6B, 0);
        if (input.Sample().Pressed != PaddleButtons.None)
            throw new InvalidOperationException("A different keyboard triggered a paddle.");
        input.ProcessKeyboardEvent(keyboard.Handle, 0x6B, 0);
        input.ProcessKeyboardEvent(keyboard.Handle, 0x6B, 1);
        var tap = input.Sample();
        if (tap.Held != PaddleButtons.None || tap.Pressed != PaddleButtons.LargeRight || input.Sample().Pressed != PaddleButtons.None)
            throw new InvalidOperationException("A complete tap between polls did not produce exactly one edge.");
        input.ProcessKeyboardEvent(keyboard.Handle, 0x6B, 0);
        var press = input.Sample();
        input.ProcessKeyboardEvent(keyboard.Handle, 0x6B, 0);
        var repeat = input.Sample();
        if (press.Pressed != PaddleButtons.LargeRight || repeat.Held != PaddleButtons.LargeRight || repeat.Pressed != PaddleButtons.None)
            throw new InvalidOperationException("A held key retriggered its paddle.");
        Console.WriteLine("PASS simulated raw events using the enumerated device handle, including a fast tap and repeat suppression.");
    }
}
finally
{
    DestroyWindow(hwnd);
}

[DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
static extern nint CreateWindowExW(uint extendedStyle, string className, string name, uint style,
    int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool DestroyWindow(nint hwnd);
