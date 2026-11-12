using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EliteScriptStudio.Diagnostics;

internal sealed record SystemPerformanceSnapshot(
    double? CpuPercent = null, ulong? MemoryUsedBytes = null, ulong? MemoryTotalBytes = null,
    double? GpuPercent = null, ulong? VramUsedBytes = null, ulong? VramTotalBytes = null, string? GpuName = null,
    double? AppCpuPercent = null, long? AppMemoryBytes = null, long? AppPrivateBytes = null, int? AppThreadCount = null,
    int LogicalProcessors = 0, DateTimeOffset? SampledAtUtc = null, long SamplesCollected = 0, bool IsSampling = false,
    string? Error = null, IReadOnlyList<SystemResourceSample>? History = null);

internal sealed record SystemResourceSample(DateTimeOffset AtUtc, double? CpuPercent, double? GpuPercent, double? AppCpuPercent);

internal interface ISystemPerformanceProbe : IDisposable
{
    SystemPerformanceSnapshot Read();
}

internal sealed class SystemPerformanceMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<ISystemPerformanceProbe> _createProbe;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _samplingStop;
    private Task _sampling = Task.CompletedTask;
    private SystemPerformanceSnapshot _latest = new();
    private bool _enabled, _disposed;
    private long _generation, _sampleCount;

    public SystemPerformanceMonitor(Func<ISystemPerformanceProbe>? createProbe = null, TimeSpan? interval = null)
    {
        _createProbe = createProbe ?? (() => new WindowsProbe());
        _interval = interval ?? TimeSpan.FromSeconds(1);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public SystemPerformanceSnapshot Latest => Volatile.Read(ref _latest);
    internal Task Completion { get { lock (_gate) return _sampling; } }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;
                long generation = ++_generation;
                _samplingStop?.Cancel(); _samplingStop = null;
                Volatile.Write(ref _latest, new(SamplesCollected: _sampleCount));
                if (!value) return;
                var cancellation = _samplingStop = new CancellationTokenSource();
                // A restart waits for the previous probe to release its native resources.
                var previous = _sampling;
                _sampling = Task.Run(() => SampleAsync(previous, generation, cancellation));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _enabled = false; _generation++;
            _samplingStop?.Cancel(); _samplingStop = null;
            Volatile.Write(ref _latest, new(SamplesCollected: _sampleCount));
        }
    }

    private async Task SampleAsync(Task previous, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await previous.ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            using var probe = _createProbe();
            using var timer = new PeriodicTimer(_interval);
            var history = new Queue<SystemResourceSample>();
            do
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var reading = probe.Read();
                var now = DateTimeOffset.UtcNow;
                lock (_gate)
                {
                    if (_disposed || !_enabled || generation != _generation) return;
                    history.Enqueue(new(now, reading.CpuPercent, reading.GpuPercent, reading.AppCpuPercent));
                    while (history.Count > 60) history.Dequeue();
                    Volatile.Write(ref _latest, reading with { IsSampling = true, SampledAtUtc = now,
                        SamplesCollected = ++_sampleCount, History = history.ToArray() });
                }
            }
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate)
                if (!_disposed && generation == _generation)
                    Volatile.Write(ref _latest, new(Error: "Resource counters unavailable: " + error.Message, SamplesCollected: _sampleCount));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_samplingStop, cancellation)) _samplingStop = null;
                cancellation.Dispose();
            }
        }
    }

    private sealed class WindowsProbe : ISystemPerformanceProbe
    {
        private readonly Process _process;
        private readonly Adapter? _adapter;
        private readonly GpuCounters? _gpu;
        private CpuTimes? _cpu;
        private TimeSpan? _appCpu;
        private long _at;
        private bool _hasSample;

        public WindowsProbe()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows resource counters are required.");
            _process = Process.GetCurrentProcess();
            try { _adapter = FindAdapter(); _gpu = new GpuCounters(_adapter); }
            catch (Exception) { /* CPU and memory readings remain available without GPU counters. */ }
        }

        public SystemPerformanceSnapshot Read()
        {
            CpuTimes? current = GetSystemTimes(out var idle, out var kernel, out var user) ? new(idle, kernel, user) : null;
            double? cpu = current is { } after && _cpu is { } before ? BusyPercent(before, after) : null;
            _cpu = current;
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            bool hasMemory = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical >= memory.AvailablePhysical;
            var (usage, vram) = _gpu?.Read(_hasSample) ?? (null, null);
            _hasSample = true;
            _process.Refresh();
            var appCpu = _process.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            double? appUsage = _appCpu is { } previousCpu
                ? ProcessPercent(previousCpu, appCpu, Stopwatch.GetElapsedTime(_at, now), Environment.ProcessorCount) : null;
            _appCpu = appCpu; _at = now;
            return new(cpu, hasMemory ? memory.TotalPhysical - memory.AvailablePhysical : null,
                hasMemory ? memory.TotalPhysical : null, usage, vram,
                _adapter?.DedicatedBytes is > 0 ? _adapter.DedicatedBytes : null, _adapter?.Name,
                appUsage, _process.WorkingSet64, _process.PrivateMemorySize64, _process.Threads.Count, Environment.ProcessorCount);
        }

        public void Dispose() { try { _gpu?.Dispose(); } finally { _process.Dispose(); } }
    }

    internal static double? ProcessPercent(TimeSpan before, TimeSpan after, TimeSpan elapsed, int processors) =>
        elapsed > TimeSpan.Zero && processors > 0 && after >= before
            ? Math.Clamp(100 * (after - before).TotalSeconds / (elapsed.TotalSeconds * processors), 0, 100) : null;

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
