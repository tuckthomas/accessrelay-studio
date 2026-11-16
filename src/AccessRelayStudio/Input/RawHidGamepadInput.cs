using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using Microsoft.Win32.SafeHandles;

namespace EliteScriptStudio.Input;

/// <summary>Reads a physical Xbox HID gamepad interface directly over USB/HID for background isolation.</summary>
internal sealed class RawHidGamepadInput : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const int HidpStatusSuccess = 0x00110000;
    private const int HidpStatusUsageNotFound = 0x00110002;
    private const ushort GenericDesktopPage = 0x0001;
    private const ushort GamepadUsage = 0x0005;
    private const ushort ButtonPage = 0x0009;
    private const ushort UsageX = 0x0030;
    private const ushort UsageY = 0x0031;
    private const ushort UsageZ = 0x0032;
    private const ushort UsageRx = 0x0033;
    private const ushort UsageRy = 0x0034;
    private const ushort UsageHat = 0x0039;
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private SafeFileHandle? _handle;
    private Guid _boundContainer;
    private ControllerState _latest;
    private bool _hasReading;
    private volatile bool _connected;
    private int _disposed;

    public bool IsBound => _connected;

    public void Bind(Guid? containerId)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (containerId is not { } target || target == Guid.Empty)
        {
            StopReader();
            _boundContainer = Guid.Empty;
            return;
        }

        if (_connected && _boundContainer == target)
            return;

        StopReader();
        _boundContainer = target;

        string? path = FindGamepadInterface(target);
        if (path is null)
            return;

        var handle = CreateFileW(path, GenericRead, FileShareReadWrite, 0, OpenExisting, FileFlagOverlapped, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }

        if (!HidD_GetPreparsedData(handle, out nint preparsed) || preparsed == 0)
        {
            handle.Dispose();
            return;
        }

        if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess || caps.UsagePage != GenericDesktopPage ||
            caps.Usage != GamepadUsage || caps.InputReportByteLength == 0)
        {
            HidD_FreePreparsedData(preparsed);
            handle.Dispose();
            return;
        }

        var cts = new CancellationTokenSource();
        _handle = handle;
        _cts = cts;
        _connected = true;
        lock (_gate)
        {
            _latest = default;
            _hasReading = false;
        }

        bool unsignedAxes = DetectUnsignedAxes(preparsed, caps.NumberInputValueCaps);
        _thread = new Thread(() => ReadLoop(handle, preparsed, caps.InputReportByteLength, unsignedAxes, cts.Token))
        {
            IsBackground = true,
            Name = "AccessRelay.RawHidGamepad",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public bool TryRead(out ControllerState state)
    {
        lock (_gate)
        {
            if (!_connected || !_hasReading)
            {
                state = default;
                return false;
            }
            state = _latest;
            return true;
        }
    }

    private void ReadLoop(SafeFileHandle handle, nint preparsed, ushort reportLength, bool unsignedAxes, CancellationToken token)
    {
        var managedBuffer = new byte[reportLength];
        var zeroBytes = new byte[reportLength];
        var usages = new ushort[32];
        using var waitEvent = new ManualResetEvent(false);
        nint waitHandle = waitEvent.SafeWaitHandle.DangerousGetHandle();
        WaitHandle[] waits = [waitEvent, token.WaitHandle];
        nint nativeBuffer = Marshal.AllocHGlobal(reportLength);
        int overlappedSize = Marshal.SizeOf<NativeOverlapped>();
        nint nativeOverlapped = Marshal.AllocHGlobal(overlappedSize);
        bool ioPending = false;
        try
        {
            while (!token.IsCancellationRequested && !handle.IsClosed && !handle.IsInvalid)
            {
                waitEvent.Reset();
                Marshal.Copy(zeroBytes, 0, nativeBuffer, reportLength);
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = waitHandle }, nativeOverlapped, false);
                ioPending = false;

                bool ok = ReadFile(handle, nativeBuffer, reportLength, nint.Zero, nativeOverlapped);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ErrorIoPending)
                        break;
                    ioPending = true;
                    if (WaitHandle.WaitAny(waits) != 0)
                    {
                        CancelIoEx(handle, nativeOverlapped);
                        GetOverlappedResult(handle, nativeOverlapped, out _, true);
                        ioPending = false;
                        return;
                    }
                }

                if (!GetOverlappedResult(handle, nativeOverlapped, out int read, true))
                {
                    ioPending = false;
                    break;
                }
                ioPending = false;

                if (read != reportLength)
                    continue;

                Marshal.Copy(nativeBuffer, managedBuffer, 0, reportLength);
                if (unsignedAxes && !HasAnyNonZero(managedBuffer, reportLength))
                    continue;

                if (TryParseReport(preparsed, managedBuffer, reportLength, usages, out var parsed))
                {
                    lock (_gate)
                    {
                        _latest = parsed;
                        _hasReading = true;
                    }
                }
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            if (ioPending && !handle.IsInvalid && !handle.IsClosed)
            {
                CancelIoEx(handle, nativeOverlapped);
                GetOverlappedResult(handle, nativeOverlapped, out _, true);
            }
            Marshal.FreeHGlobal(nativeOverlapped);
            Marshal.FreeHGlobal(nativeBuffer);
            _connected = false;
            HidD_FreePreparsedData(preparsed);
        }
    }

    internal static bool HasAnyNonZero(byte[] buffer, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (buffer[i] != 0)
                return true;
        }
        return false;
    }

    private static bool DetectUnsignedAxes(nint preparsed, ushort valueCapsCount)
    {
        if (valueCapsCount == 0)
            return false;
        ushort count = valueCapsCount;
        var caps = new HidpValueCaps[count];
        if (HidP_GetValueCaps(0, caps, ref count, preparsed) != HidpStatusSuccess)
            return false;
        for (int i = 0; i < count; i++)
        {
            ref readonly var c = ref caps[i];
            if (c.UsagePage == GenericDesktopPage &&
                c.UsageMin is UsageX or UsageY or UsageRx or UsageRy &&
                c.LogicalMin >= 0 && c.LogicalMax >= 255)
                return true;
        }
        return false;
    }

    internal static bool TryParseReport(nint preparsed, byte[] report, uint reportLength, ushort[] usages, out ControllerState state)
    {
        state = default;
        uint usageCount = (uint)usages.Length;
        int status = HidP_GetUsages(0, ButtonPage, 0, usages, ref usageCount, preparsed, report, reportLength);
        if (status != HidpStatusSuccess && status != 0x00110002) // HIDP_STATUS_USAGE_NOT_FOUND
            return false;

        ushort buttons = 0;
        for (int i = 0; i < (int)usageCount; i++)
        {
            buttons |= usages[i] switch
            {
                1 => (ushort)0x1000, // A
                2 => (ushort)0x2000, // B
                3 => (ushort)0x4000, // X
                4 => (ushort)0x8000, // Y
                5 => (ushort)0x0100, // LB
                6 => (ushort)0x0200, // RB
                7 => (ushort)0x0020, // View
                8 => (ushort)0x0010, // Menu
                9 => (ushort)0x0040, // LS
                10 => (ushort)0x0080, // RS
                _ => (ushort)0
            };
        }

        if (HidP_GetUsageValue(0, GenericDesktopPage, 0, UsageHat, out uint hat, preparsed, report, reportLength) == HidpStatusSuccess)
        {
            buttons |= hat switch
            {
                1 => (ushort)0x0001, // Up
                2 => (ushort)0x0009, // Up + Right
                3 => (ushort)0x0008, // Right
                4 => (ushort)0x000A, // Down + Right
                5 => (ushort)0x0002, // Down
                6 => (ushort)0x0006, // Down + Left
                7 => (ushort)0x0004, // Left
                8 => (ushort)0x0005, // Up + Left
                _ => (ushort)0
            };
        }

        short lx = ReadAxis(preparsed, report, reportLength, UsageX, invert: false);
        short ly = ReadAxis(preparsed, report, reportLength, UsageY, invert: true);
        short rx = ReadAxis(preparsed, report, reportLength, UsageRx, invert: false);
        short ry = ReadAxis(preparsed, report, reportLength, UsageRy, invert: true);

        byte lt = 0, rt = 0;
        if (HidP_GetUsageValue(0, GenericDesktopPage, 0, UsageZ, out uint z, preparsed, report, reportLength) == HidpStatusSuccess)
        {
            if (z > 32768) lt = (byte)Math.Clamp((z - 32768) >> 7, 0, 255);
            else if (z < 32768) rt = (byte)Math.Clamp((32768 - z) >> 7, 0, 255);
        }

        state = new ControllerState(buttons, lt, rt, lx, ly, rx, ry);
        return true;
    }

    private static short ReadAxis(nint preparsed, byte[] report, uint reportLength, ushort usage, bool invert)
    {
        if (HidP_GetUsageValue(0, GenericDesktopPage, 0, usage, out uint raw, preparsed, report, reportLength) != HidpStatusSuccess)
            return 0;
        int centered = invert ? 32768 - (int)raw : (int)raw - 32768;
        return (short)Math.Clamp(centered, short.MinValue, short.MaxValue);
    }

    private static string? FindGamepadInterface(Guid containerId)
    {
        var iface = HidInterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref iface, null, 0) != 0 || length < 2)
            return null;
        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref iface, null, buffer, length, 0) != 0)
            return null;

        foreach (string path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (PhysicalGamepadInventory.IsVirtualNode(path) ||
                path.EndsWith(@"\KBD", StringComparison.OrdinalIgnoreCase))
                continue;

            string instance = ToInstanceId(path);
            if (!PhysicalGamepadInventory.TryGetContainerId(instance, out var nodeContainer) || nodeContainer != containerId)
                continue;

            using var probe = CreateFileW(path, GenericRead, FileShareReadWrite, 0, OpenExisting, 0, 0);
            if (probe.IsInvalid)
                continue;
            if (!HidD_GetPreparsedData(probe, out nint ppd) || ppd == 0)
                continue;
            try
            {
                if (HidP_GetCaps(ppd, out var caps) == HidpStatusSuccess &&
                    caps.UsagePage == GenericDesktopPage && caps.Usage == GamepadUsage)
                    return path;
            }
            finally { HidD_FreePreparsedData(ppd); }
        }
        return null;
    }

    private static string ToInstanceId(string deviceInterfacePath)
    {
        string trimmed = deviceInterfacePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? deviceInterfacePath[4..] : deviceInterfacePath;
        int guidMarker = trimmed.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidMarker > 0)
            trimmed = trimmed[..guidMarker];
        return trimmed.Replace('#', '\\');
    }

    private void StopReader()
    {
        _connected = false;
        var cts = _cts;
        _cts = null;
        var thread = _thread;
        _thread = null;
        var handle = _handle;
        _handle = null;

        cts?.Cancel();
        if (thread is not null && thread.IsAlive && !thread.Join(500))
        {
            handle?.Dispose();
            thread.Join(500);
        }
        handle?.Dispose();
        cts?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        StopReader();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportID, IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize, ReportCount;
        public ushort Reserved2_0, Reserved2_1, Reserved2_2, Reserved2_3, Reserved2_4;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid iface, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid iface, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, nint buffer, int bytesToRead, nint bytesRead, nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, nint overlapped, out int bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, nint overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] HidpValueCaps[] valueCaps, ref ushort valueCapsLength, nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength, nint preparsedData, [In] byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, nint preparsedData, [In] byte[] report, uint reportLength);
}
