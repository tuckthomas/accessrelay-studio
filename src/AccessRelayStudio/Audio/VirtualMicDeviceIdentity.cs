using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EliteScriptStudio.Audio;

internal static class VirtualMicDeviceIdentity
{
    private const string IdentityFile = "AccessRelay.VirtualMicUsbId.txt";
    private static readonly string? UsbInstance = ReadUsbInstance();

    internal static bool IsConfigured => UsbInstance is not null;

    public static bool MatchesEndpoint(string endpointId)
    {
        if (UsbInstance is null || CM_Locate_DevNodeW(out uint node, @"SWD\MMDEVAPI\" + endpointId, 0) != 0)
            return false;

        var deviceId = new StringBuilder(200);
        for (int level = 0; level < 16; level++)
        {
            if (CM_Get_Device_IDW(node, deviceId, (uint)deviceId.Capacity, 0) != 0)
                return false;
            if (deviceId.ToString().Equals(UsbInstance, StringComparison.OrdinalIgnoreCase))
                return true;
            if (CM_Get_Parent(out node, node, 0) != 0)
                return false;
        }
        return false;
    }

    private static string? ReadUsbInstance()
    {
        string identity;
        try
        {
            identity = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VirtualMicrophone", IdentityFile)).Trim();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        // pid.codes permits this shared VID/PID only in private, in-house test builds.
        if (identity == "1209:0001 PRIVATE-TEST")
            return @"USB\VID_1209&PID_0001\AccessRelay-VirtualMic-v1";
        if (identity.Length != 9 || identity[4] != ':'
            || !ushort.TryParse(identity.AsSpan(0, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort vid)
            || !ushort.TryParse(identity.AsSpan(5, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort pid)
            || vid is 0 or ushort.MaxValue || pid is 0 or ushort.MaxValue || (vid == 0x1209 && pid < 0x2000))
            return null;
        return $@"USB\VID_{vid:X4}&PID_{pid:X4}\AccessRelay-VirtualMic-v1";
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, [Out] StringBuilder buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
