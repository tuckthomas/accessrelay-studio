using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EliteScriptStudio.Input;

internal readonly record struct HardwareId(ushort Vendor, ushort Product);

internal readonly record struct PhysicalGamepad(HardwareId Hardware, Guid Container, string Transport);

internal sealed record GamepadInventory(PhysicalGamepad[] Physical, IReadOnlySet<HardwareId> Virtual)
{
    public static readonly GamepadInventory Empty = new([], new HashSet<HardwareId>());
}

/// <summary>Checks WGI identities against present devices on physical Windows buses.</summary>
internal static partial class PhysicalGamepadInventory
{
    private const uint PresentOnly = 0x00000100;
    private const uint Success = 0;
    private const uint GuidProperty = 0x0000000D;
    private static readonly DevicePropertyKey ContainerId = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    public static GamepadInventory Find(IReadOnlySet<HardwareId> requested)
    {
        if (requested.Count == 0)
            return GamepadInventory.Empty;

        var devices = new HashSet<PhysicalGamepad>();
        var virtualIds = new HashSet<HardwareId>();
        foreach (var instance in GetPresentDeviceIds())
        {
            if (!TryReadHardwareId(instance, out var hardware) || !requested.Contains(hardware) ||
                CM_Locate_DevNodeW(out uint node, instance, 0) != Success)
                continue;

            string? transport = PhysicalTransport(node, out bool isVirtual);
            if (isVirtual)
                virtualIds.Add(hardware);
            if (transport is null)
                continue;

            uint size = 16;
            var key = ContainerId;
            if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, out var container, ref size, 0) == Success &&
                type == GuidProperty && container != Guid.Empty)
                devices.Add(new(hardware, container, transport));
        }
        return new(devices.ToArray(), virtualIds);
    }

    internal static bool TryReadHardwareId(string instance, out HardwareId hardware)
    {
        var match = HardwarePattern().Match(instance);
        if (!match.Success)
        {
            hardware = default;
            return false;
        }
        hardware = new(
            ushort.Parse(match.Groups["vendor"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(match.Groups["product"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    private static string? PhysicalTransport(uint node, out bool isVirtual)
    {
        isVirtual = false;
        bool usbHub = false;
        bool bluetooth = false;
        bool hardwareBus = false;
        for (int i = 0; i < 32; i++)
        {
            if (CM_Get_Device_ID_Size(out uint length, node, 0) != Success)
                return null;
            var buffer = new StringBuilder((int)length + 1);
            if (CM_Get_Device_IDW(node, buffer, length + 1, 0) != Success)
                return null;
            string id = buffer.ToString();
            if (IsVirtualNode(id))
            {
                isVirtual = true;
                return null;
            }

            usbHub |= id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);
            bluetooth |= id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
            hardwareBus |= id.StartsWith(@"PCI\VEN_", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase);

            if (CM_Get_Parent(out node, node, 0) != Success)
                return ClassifyTransport(usbHub, bluetooth, hardwareBus);
        }
        return null;
    }

    internal static string? ClassifyTransport(bool usbHub, bool bluetooth, bool hardwareBus) =>
        bluetooth && (usbHub || hardwareBus) ? "Bluetooth" : usbHub ? "USB" : null;

    internal static bool IsVirtualNode(string id) =>
        id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("VIGEM", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("VJOY", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("USBIP", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("SYNTHETICGAMEPAD", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase);

    private static string[] GetPresentDeviceIds()
    {
        // Hot-plug can change the size between the two native calls.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, null, PresentOnly) != Success || length < 2)
                return [];
            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW(null, buffer, length, PresentOnly) == Success)
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    [GeneratedRegex(@"(?:^|[\&\\_])VID(?:_|&[0-9A-F]{4})(?<vendor>[0-9A-F]{4})[&_]+PID[_&](?<product>[0-9A-F]{4})(?=$|[&\\_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HardwarePattern();

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DevicePropertyKey(Guid Format, uint Property);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint node, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, [Out] StringBuilder buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref DevicePropertyKey key, out uint type,
        out Guid buffer, ref uint size, uint flags);
}
