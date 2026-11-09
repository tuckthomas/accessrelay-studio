using System.Runtime.InteropServices;

namespace EliteScriptStudio.Diagnostics;

internal sealed record SystemPerformanceSnapshot(
    double? CpuPercent = null, ulong? MemoryUsedBytes = null, ulong? MemoryTotalBytes = null,
    double? GpuPercent = null, ulong? VramUsedBytes = null, ulong? VramTotalBytes = null, string? GpuName = null);

internal sealed class SystemPerformanceMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;
    private SystemPerformanceSnapshot _latest = new();
    private volatile bool _enabled = true;
    private int _resetBaseline;
    private int _disposed;

    public SystemPerformanceMonitor() => _sampling = Task.Run(SampleAsync);

    public SystemPerformanceSnapshot Latest => Volatile.Read(ref _latest);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Interlocked.Exchange(ref _resetBaseline, 1);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _ = _sampling.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task SampleAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var adapter = FindAdapter();
        using var gpu = new GpuCounters(adapter);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        CpuTimes? previous = null;
        try
        {
            do
            {
                if (Interlocked.Exchange(ref _resetBaseline, 0) != 0 || !Enabled)
                    previous = null;
                if (!Enabled)
                    continue;

                bool hasBaseline = previous.HasValue;
                CpuTimes? current = GetSystemTimes(out var idle, out var kernel, out var user)
                    ? new(idle, kernel, user) : null;
                double? cpu = current is { } after && previous is { } before ? BusyPercent(before, after) : null;
                previous = current;

                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                bool hasMemory = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical >= memory.AvailablePhysical;
                var (usage, vram) = gpu.Read(hasBaseline);
                Volatile.Write(ref _latest, new SystemPerformanceSnapshot(cpu,
                    hasMemory ? memory.TotalPhysical - memory.AvailablePhysical : null,
                    hasMemory ? memory.TotalPhysical : null, usage, vram,
                    adapter?.DedicatedBytes is > 0 ? adapter.DedicatedBytes : null, adapter?.Name));
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    internal static double? BusyPercent(CpuTimes before, CpuTimes after)
    {
        if (after.Idle < before.Idle || after.Kernel < before.Kernel || after.User < before.User)
            return null;

        double total = (double)(after.Kernel - before.Kernel) + (after.User - before.User);
        return total > 0 ? Math.Clamp(100 * (1 - (after.Idle - before.Idle) / total), 0, 100) : null;
    }

    internal static double? BusiestEngine(IEnumerable<(string Instance, double Value)> counters, string adapterKey)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in counters)
        {
            int adapter = instance.IndexOf(adapterKey, StringComparison.OrdinalIgnoreCase);
            int engine = adapter < 0 ? -1 : instance.IndexOf("_eng_", adapter, StringComparison.OrdinalIgnoreCase);
            if (engine < 0 || !double.IsFinite(value) || value < 0)
                continue;

            int end = instance.IndexOf('_', engine + "_eng_".Length);
            string key = end < 0 ? instance[adapter..] : instance[adapter..end];
            engines[key] = engines.GetValueOrDefault(key) + value;
        }
        return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    private sealed record Adapter(string Name, string CounterKey, ulong DedicatedBytes);

    private static Adapter? FindAdapter()
    {
        // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 vtable slots.
        const int enumAdapters1 = 12;
        const int getDesc1 = 10;
        const uint software = 2;
        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref factoryId, out var factory) < 0)
            return null;

        Adapter? selected = null;
        try
        {
            var enumerate = ComMethod<EnumAdapter>(factory, enumAdapters1);
            for (uint index = 0; enumerate(factory, index, out var pointer) >= 0; index++)
            {
                try
                {
                    if (ComMethod<GetAdapterDescription>(pointer, getDesc1)(pointer, out var description) < 0
                        || (description.Flags & software) != 0)
                        continue;

                    ulong bytes = description.DedicatedVideoMemory.ToUInt64();
                    if (selected is null || bytes > selected.DedicatedBytes)
                    {
                        string key = $"luid_0x{description.LuidHigh:x8}_0x{description.LuidLow:x8}_phys_";
                        selected = new(description.Description, key, bytes);
                    }
                }
                finally { Marshal.Release(pointer); }
            }
        }
        finally { Marshal.Release(factory); }
        return selected;
    }

    private static T ComMethod<T>(IntPtr pointer, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size));

    private sealed class GpuCounters : IDisposable
    {
        private const uint FormatDouble = 0x200;
        private const uint NoScale = 0x1000;
        private const uint MoreData = 0x800007d2;
        private readonly Adapter? _adapter;
        private readonly IntPtr _query;
        private readonly IntPtr _engines;
        private readonly IntPtr _memory;

        public GpuCounters(Adapter? adapter)
        {
            _adapter = adapter;
            if (adapter is null || PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
                return;

            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engines) != 0)
                _engines = IntPtr.Zero;
            if (PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memory) != 0)
                _memory = IntPtr.Zero;
        }

        public (double? Usage, ulong? Memory) Read(bool hasBaseline)
        {
            if (_adapter is not { } adapter || _query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
                return (null, null);

            double? usage = hasBaseline ? BusiestEngine(ReadCounter(_engines, FormatDouble), adapter.CounterKey) : null;
            double? memory = null;
            foreach (var (instance, value) in ReadCounter(_memory, FormatDouble | NoScale))
            {
                if (instance.StartsWith(adapter.CounterKey, StringComparison.OrdinalIgnoreCase) && value >= 0)
                    memory = (memory ?? 0) + value;
            }
            return (usage, memory is >= 0 and < ulong.MaxValue ? (ulong)memory.Value : null);
        }

        private static IEnumerable<(string Instance, double Value)> ReadCounter(IntPtr counter, uint format)
        {
            uint bytes = 0;
            if (counter == IntPtr.Zero || PdhGetFormattedCounterArray(counter, format, ref bytes, out _, IntPtr.Zero) != MoreData
                || bytes == 0 || bytes > 4 * 1024 * 1024)
                yield break;

            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArray(counter, format, ref bytes, out uint count, buffer) != 0)
                    yield break;

                int size = Marshal.SizeOf<CounterItem>();
                for (int index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + index * size);
                    if (item.Status is 0 or 1 && double.IsFinite(item.Value)
                        && Marshal.PtrToStringUni(item.Name) is { } name)
                        yield return (name, item.Value);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
                PdhCloseQuery(_query);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow, LuidHigh, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapter(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDescription(IntPtr adapter, out AdapterDescription description);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid factoryId, out IntPtr factory);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
    private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW", ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
