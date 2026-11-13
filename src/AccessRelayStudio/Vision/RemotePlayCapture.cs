using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace EliteScriptStudio.Vision;

internal sealed record CaptureSource(nint Hwnd, string Title, uint ProcessId)
{
    public override string ToString() => Title;
}

internal readonly record struct CaptureStatus(bool IsRunning, string Message, long SourceGeneration,
    long LatestFrameTicks, long LatestSequence, long ContentChangedTicks, int Width, int Height, long DroppedFrames);

/// <summary>HWND capture with a free-threaded WGC pool and explicitly measured CPU readback.
/// Consumers must dispose frame leases. This does not measure original console frame age.</summary>
internal sealed class RemotePlayCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly LatestFrameBuffer _frames = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private Resources? _resources;
    private bool _disposed;
    private long _generation, _sequence, _latestTicks, _contentChangedTicks;
    private ulong _lastContentHash;
    private string _message = "Capture is off.";
    private int _width, _height;

    public WaitHandle FrameReady => _frameReady;
    internal CaptureD3D11 GraphicsDevice { get { lock (_gate) return _resources?.Graphics ?? throw new InvalidOperationException("Capture is not running."); } }
    public CaptureStatus Status
    {
        get
        {
            // Reading UI/eligibility status must not wait on D3D staging Map or pixel readback.
            var resource = Volatile.Read(ref _resources);
            bool present = resource is not null && IsWindow(resource.Hwnd);
            bool running = present && !IsIconic(resource!.Hwnd);
            string message = resource is not null && !present ? "The captured window closed."
                : present && !running ? "Capture paused: restore the source window." : Volatile.Read(ref _message);
            return new(running, message, Interlocked.Read(ref _generation), Interlocked.Read(ref _latestTicks),
                Interlocked.Read(ref _sequence), resource?.GpuFrames?.ContentChangedTicks ?? Interlocked.Read(ref _contentChangedTicks),
                Volatile.Read(ref _width), Volatile.Read(ref _height), resource?.GpuFrames?.DroppedFrames ?? _frames.DroppedFrames);
        }
    }

    public static IReadOnlyList<CaptureSource> EnumerateWindows()
    {
        var sources = new List<CaptureSource>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != 0)
                return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (process == Environment.ProcessId)
                return true;
            int length = GetWindowTextLength(hwnd);
            if (length is <= 0 or > 4096)
                return true;
            var title = new StringBuilder(length + 1);
            if (GetWindowText(hwnd, title, title.Capacity) == 0)
                return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0)
                return true;
            sources.Add(new(hwnd, title.ToString(), process));
            return true;
        }, 0);
        return sources.OrderBy(source => source.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Start(nint hwnd, bool gpuFrames = false, nint adapter = 0)
    {
        Stop();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("Windows Graphics Capture is unavailable on this computer.");
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                throw new InvalidOperationException("Select a visible, restored application window.");
            Resources? resource = null;
            try
            {
                var graphics = new CaptureD3D11(adapter: adapter);
                resource = new(hwnd, graphics);
                if (gpuFrames) resource.GpuFrames = new GpuCaptureBuffer(graphics);
                resource.Item = CaptureD3D11.CreateItem(hwnd);
                resource.Size = resource.Item.Size;
                ValidateSize(resource.Size);
                resource.Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphics.Device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, resource.Size);
                resource.Session = resource.Pool.CreateCaptureSession(resource.Item);
                resource.Session.IsCursorCaptureEnabled = false;
                resource.Pool.FrameArrived += FrameArrived;
                resource.Item.Closed += ItemClosed;
                _resources = resource;
                _generation++;
                _latestTicks = _contentChangedTicks = 0;
                _lastContentHash = 0;
                _width = resource.Size.Width;
                _height = resource.Size.Height;
                _message = "Waiting for the first captured frame.";
                resource.Session.StartCapture();
            }
            catch
            {
                _resources = null;
                resource?.Dispose();
                _message = "Capture could not start.";
                throw;
            }
        }
    }

    public bool TryAcquireLatest(out CapturedVisionFrame? frame)
    {
        lock (_gate)
        {
            if (_resources is null || !IsWindow(_resources.Hwnd) || IsIconic(_resources.Hwnd))
            {
                if (_resources is { } paused)
                    PauseResource(paused, IsWindow(paused.Hwnd) ? "Capture paused: restore the source window." : "The captured window closed.");
                _frames.Clear();
                _resources?.GpuFrames?.Clear();
                frame = null;
                return false;
            }
            return _resources.GpuFrames is { } gpu ? gpu.TryAcquireLatest(out frame) : _frames.TryAcquireLatest(out frame);
        }
    }

    public void Stop() => Stop("Capture is off.");

    private void Stop(string message, GraphicsCaptureItem? expectedItem = null)
    {
        Resources? resource;
        lock (_gate)
        {
            if (expectedItem is not null && !ReferenceEquals(_resources?.Item, expectedItem))
                return;
            resource = _resources;
            _resources = null;
            _frames.Clear();
            resource?.GpuFrames?.Clear();
            _generation++;
            _latestTicks = _contentChangedTicks = 0;
            _message = message;
            if (!_disposed) _frameReady.Set();
        }
        if (resource is not null)
        {
            if (resource.Pool is { } pool) pool.FrameArrived -= FrameArrived;
            if (resource.Item is { } item) item.Closed -= ItemClosed;
            resource.Dispose();
        }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => Stop("The captured window closed. Select a source again.", sender);

    private void PauseResource(Resources resource, string reason)
    {
        if (!resource.Paused)
        {
            resource.Paused = true;
            _generation++;
            _latestTicks = _contentChangedTicks = 0;
            _frames.Clear();
            resource.GpuFrames?.Clear();
            _frameReady.Set();
        }
        _message = reason;
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Resources? failed = null;
        lock (_gate)
        {
            if (_disposed || _resources is not { } resource || !ReferenceEquals(resource.Pool, sender))
                return;
            try
            {
                bool published = false;
                SizeInt32? resize = null;
                using (var frame = TakeNewestFrame(sender))
                {
                    if (frame is null)
                        return;
                    // Timestamp actual local receipt, before CPU/GPU copies. WGC's compositor
                    // timestamp can lead receipt on live sources; retain it without clamping it
                    // or using it as the processing/deadline clock.
                    long captureTicks = Stopwatch.GetTimestamp();
                    if (!IsWindow(resource.Hwnd) || IsIconic(resource.Hwnd))
                    {
                        PauseResource(resource, "Capture paused: restore the source window.");
                        return;
                    }
                    resource.Paused = false;
                    var size = frame.ContentSize;
                    if (size.Width != resource.Size.Width || size.Height != resource.Size.Height)
                    {
                        ValidateSize(size);
                        resize = size;
                        _frames.Clear();
                        resource.GpuFrames?.Clear();
                        _generation++;
                        _latestTicks = _contentChangedTicks = 0;
                        _width = size.Width;
                        _height = size.Height;
                        _frameReady.Set();
                    }
                    else
                    {
                        long compositorTicks = checked((long)(frame.SystemRelativeTime.Ticks *
                            ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond)));
                        published = resource.GpuFrames is { } gpu
                            ? resource.Graphics.CopyGpuFrame(frame, gpu, captureTicks, ++_sequence, _generation, compositorTicks)
                            : resource.Graphics.CopyFrame(frame, _frames, captureTicks, ++_sequence, _generation, compositorTicks);
                        if (published)
                        {
                            _latestTicks = captureTicks;
                            if (resource.GpuFrames is null)
                            {
                                ulong fingerprint = _frames.LatestFingerprint;
                                if (_contentChangedTicks == 0 || fingerprint != _lastContentHash) _contentChangedTicks = captureTicks;
                                _lastContentHash = fingerprint;
                            }
                            _message = "Capturing application frames. Console video age is not measured.";
                        }
                    }
                }
                if (resize is { } nextSize)
                {
                    resource.Size = nextSize;
                    sender.Recreate(resource.Graphics.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, nextSize);
                }
                if (published) _frameReady.Set();
            }
            catch (Exception error)
            {
                failed = _resources;
                _resources = null;
                _frames.Clear();
                _generation++;
                _latestTicks = _contentChangedTicks = 0;
                _message = $"Capture stopped: {error.Message}";
                _frameReady.Set();
            }
        }
        // Dispose outside the gate so queued WGC callbacks can observe the detached generation.
        if (failed is not null)
        {
            var resource = failed;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (resource.Pool is { } pool) pool.FrameArrived -= FrameArrived;
                    if (resource.Item is { } item) item.Closed -= ItemClosed;
                    resource.Dispose();
                }
                catch (Exception) { /* Capture is already faulted; never crash a ThreadPool thread during cleanup. */ }
            });
        }
    }

    private Direct3D11CaptureFrame? TakeNewestFrame(Direct3D11CaptureFramePool pool)
    {
        var newest = pool.TryGetNextFrame();
        try
        {
            for (int attempts = 0; attempts < 8; attempts++)
            {
                var next = pool.TryGetNextFrame();
                if (next is null) break;
                newest?.Dispose();
                newest = next;
                if (_resources?.GpuFrames is { } gpu) gpu.NoteDroppedFrame();
                else _frames.NoteDroppedFrame();
            }
            return newest;
        }
        catch { newest?.Dispose(); throw; }
    }

    private static void ValidateSize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0 || checked((long)size.Width * size.Height * 4) > LatestFrameBuffer.MaximumFrameBytes)
            throw new InvalidDataException("Restore or resize the capture window to a nonempty size of 4K or smaller.");
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _frames.Dispose();
            _frameReady.Dispose();
        }
    }

    private sealed class Resources(nint hwnd, CaptureD3D11 graphics) : IDisposable
    {
        internal nint Hwnd { get; } = hwnd;
        internal CaptureD3D11 Graphics { get; } = graphics;
        internal GraphicsCaptureItem? Item;
        internal Direct3D11CaptureFramePool? Pool;
        internal GpuCaptureBuffer? GpuFrames;
        internal GraphicsCaptureSession? Session;
        internal SizeInt32 Size;
        internal bool Paused;
        public void Dispose()
        {
            Session?.Dispose();
            Session = null;
            Pool?.Dispose();
            Pool = null;
            GpuFrames?.Dispose();
            GpuFrames = null;
            Graphics.Dispose();
            Item = null;
        }
    }

    private delegate bool EnumWindow(nint hwnd, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
