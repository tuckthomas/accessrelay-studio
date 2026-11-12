using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteScriptStudio.Vision;

if (args.Contains("--preview-source", StringComparer.Ordinal))
{
    PreviewSessionChecks.RunSource();
    return;
}
if (args is ["--preview-diagnostics", var diagnosticsPath])
{
    PreviewSessionChecks.Run(diagnosticsPath);
    return;
}
if (args.Contains("--preview-only", StringComparer.Ordinal))
{
    PreviewSessionChecks.Run();
    return;
}

BufferCheck();
if (!args.Contains("--buffer-only", StringComparer.Ordinal))
{
    DatasetChecks.Run();
    RuntimeTrainingChecks.Run();
    if (!args.Contains("--no-capture", StringComparer.Ordinal))
    {
        CaptureCheck();
        PreviewSessionChecks.Run();
    }
}

static void Check(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
}

static void BufferCheck()
{
    using var buffer = new LatestFrameBuffer();
    nint mapped = Marshal.AllocHGlobal(72);
    try
    {
        var data = new byte[72];
        Array.Fill(data, (byte)17);
        Marshal.Copy(data, 0, mapped, data.Length);
        Check(buffer.PublishMapped(mapped, 24, 4, 3, 101, 1, 1), "Publish A failed.");
        Check(buffer.TryAcquireLatest(out var first), "Acquire A failed.");
        Check(first!.Buffer.Length == 48 && first.Stride == 16 && first.Pixels.Span[^1] == 17, "Row pitch was not removed.");
        Check(buffer.PublishMapped(mapped, 24, 4, 3, 102, 2, 1), "Publish B failed.");
        Check(buffer.TryAcquireLatest(out var second), "Acquire B failed.");
        Check(buffer.PublishMapped(mapped, 24, 4, 3, 103, 3, 1), "Publish C failed.");
        Check(buffer.TryAcquireLatest(out var third), "Acquire C failed.");
        Array.Fill(data, (byte)42);
        Marshal.Copy(data, 0, mapped, data.Length);
        Check(!buffer.PublishMapped(mapped, 24, 4, 3, 104, 4, 1), "All leased slots should drop new frames.");
        Check(first.Pixels.Span[0] == 17 && second!.Pixels.Span[0] == 17 && third!.Pixels.Span[0] == 17, "In-flight buffers were overwritten.");
        first.Dispose(); first.Dispose();
        Check(buffer.PublishMapped(mapped, 24, 4, 3, 104, 4, 1), "Released slot was not reused.");
        Check(buffer.PublishMapped(mapped, 24, 4, 3, 105, 5, 1), "Pending newest frame was not replaced.");
        Check(buffer.TryAcquireLatest(out var latest) && latest!.Sequence == 5, "Consumer did not receive newest frame.");
        buffer.Dispose();
        Check(latest!.Pixels.Span[0] == 42 && second!.Pixels.Span[0] == 17, "Dispose invalidated outstanding leases.");
        second!.Dispose(); third!.Dispose(); latest.Dispose();
        Check(!buffer.TryAcquireLatest(out _), "Disposed buffer still returned data.");
        Console.WriteLine("SUCCESS: bounded buffer, row pitch, latest-frame replacement, lease ownership and disposal verified.");
    }
    finally { Marshal.FreeHGlobal(mapped); }
}

static void CaptureCheck()
{
    int apartment = Native.RoInitialize(1);
    Marshal.ThrowExceptionForHR(apartment);
    string className = "AccessRelayCaptureCheck_" + Environment.ProcessId;
    Native.WindowProc callback = (hwnd, message, wparam, lparam) =>
    {
        if (message == 0x113)
        {
            Native.InvalidateRect(hwnd, 0, false);
            return 0;
        }
        if (message == 0xF)
        {
            nint dc = Native.BeginPaint(hwnd, out var paint);
            nint brush = Native.CreateSolidBrush((uint)(Environment.TickCount & 0x00FFFFFF));
            Native.FillRect(dc, ref paint.Rectangle, brush);
            Native.DeleteObject(brush);
            Native.EndPaint(hwnd, ref paint);
            return 0;
        }
        return Native.DefWindowProc(hwnd, message, wparam, lparam);
    };
    nint instance = Native.GetModuleHandle(null);
    var windowClass = new Native.WindowClass { Callback = callback, Instance = instance, ClassName = className };
    Check(Native.RegisterClass(ref windowClass) != 0, "RegisterClass failed.");
    nint window = Native.CreateWindowEx(0, className, "AccessRelay capture verification", 0x10CF0000,
        40, 40, 320, 240, 0, 0, instance, 0);
    Check(window != 0, "CreateWindowEx failed.");
    try
    {
        Native.UpdateWindow(window);
        Native.SetTimer(window, 1, 16, 0);
        using var capture = new RemotePlayCapture();
        capture.Start(window);
        using var first = WaitFrame(capture);
        long age = Stopwatch.GetTimestamp() - first.CaptureTicks;
        Check(age >= 0 && age < 5 * Stopwatch.Frequency, "Capture timestamp is not on the QPC timeline.");
        Check(first.Width > 100 && first.Height > 100 && first.Pixels.Span.AnyNonzero(), "Captured pixels were empty.");
        int initialWidth = first.Width;
        long initialGeneration = first.SourceGeneration;
        Native.SetWindowPos(window, 0, 0, 0, 380, 280, 0x6);
        CapturedVisionFrame? resized = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            var candidate = WaitFrame(capture);
            Console.WriteLine($"Resize sample: {candidate.Width}x{candidate.Height}, generation {candidate.SourceGeneration}.");
            if (candidate.Width != initialWidth) { resized = candidate; break; }
            candidate.Dispose();
        }
        using (resized)
            Check(resized is not null && resized.SourceGeneration > initialGeneration, "Resize did not invalidate the generation.");
        Native.ShowWindow(window, 6);
        Pump();
        Check(!capture.Status.IsRunning && !capture.TryAcquireLatest(out _), "Minimized source remained eligible.");
        Native.ShowWindow(window, 9);
        using var restored = WaitFrame(capture);
        Native.DestroyWindow(window);
        window = 0;
        Pump();
        Check(!capture.Status.IsRunning, "Closed window remained running.");
        Console.WriteLine($"SUCCESS: WGC synthetic-window BGRA capture, QPC timestamps, resize, minimize/restore, close. Initial frame {initialWidth}x{first.Height}.");
        GC.KeepAlive(callback);
    }
    finally
    {
        if (window != 0) Native.DestroyWindow(window);
        Native.UnregisterClass(className, instance);
        Native.RoUninitialize();
        GC.KeepAlive(callback);
    }
}

static CapturedVisionFrame WaitFrame(RemotePlayCapture capture)
{
    var timeout = Stopwatch.StartNew();
    while (timeout.Elapsed < TimeSpan.FromSeconds(5))
    {
        Pump();
        if (capture.TryAcquireLatest(out var frame)) return frame!;
        if (!capture.Status.IsRunning) throw new InvalidOperationException(capture.Status.Message);
        capture.FrameReady.WaitOne(5);
    }
    throw new TimeoutException("No captured frame: " + capture.Status.Message);
}

static void Pump()
{
    while (Native.PeekMessage(out var message, 0, 0, 0, 1))
    {
        Native.TranslateMessage(ref message);
        Native.DispatchMessage(ref message);
    }
}

static class PixelChecks
{
    internal static bool AnyNonzero(this ReadOnlySpan<byte> pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0) return true;
        return false;
    }
}

static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WindowProc(nint hwnd, uint message, nint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass { internal uint Style; internal WindowProc Callback; internal int ClassExtra, WindowExtra; internal nint Instance, Icon, Cursor, Background; internal string? MenuName; internal string ClassName; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint { internal nint Dc; internal int Erase; internal Rect Rectangle; internal int Restore, Update; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { internal nint Hwnd; internal uint Id; internal nint Wparam, Lparam; internal uint Time; internal int X, Y; internal uint Private; }
    [DllImport("combase.dll")] internal static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] internal static extern void RoUninitialize();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(uint exStyle, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetTimer(nint hwnd, nint id, uint interval, nint callback);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint hwnd, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint hwnd, ref Paint paint);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
}
