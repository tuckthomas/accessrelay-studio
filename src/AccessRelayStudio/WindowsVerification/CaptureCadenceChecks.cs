using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class CaptureCadenceChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static void Run(string evidenceDirectory, string takeDirectory)
    {
        string output = Path.GetFullPath(evidenceDirectory), take = Path.GetFullPath(takeDirectory);
        Directory.CreateDirectory(output);
        string dataset = Path.GetDirectoryName(take)!;
        using var stop = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(dataset) { NotifyFilter = NotifyFilters.DirectoryName };
        watcher.Created += (_, args) => { if (Path.GetFileName(args.FullPath).StartsWith("take-", StringComparison.Ordinal)
            && !Path.GetFileName(args.FullPath).Contains("tracked", StringComparison.Ordinal)) stop.Cancel(); };
        watcher.EnableRaisingEvents = true;
        foreach (var folder in Directory.EnumerateDirectories(dataset, "take-*"))
            if (Directory.GetCreationTimeUtc(folder) > DateTime.UtcNow.AddMinutes(-2)
                && !File.Exists(Path.Combine(folder, "controller-trace.json")))
                throw new InvalidOperationException("A recording may be active. No performance probe started.");
        var resize = ResizeCheck(take, stop.Token);
        File.WriteAllText(Path.Combine(output, "resize.json"), JsonSerializer.Serialize(resize, Json));
        Console.WriteLine(JsonSerializer.Serialize(resize, Json));
        Marshal.ThrowExceptionForHR(RoInitialize(1));
        try
        {
            var sources = RemotePlayCapture.EnumerateWindows().Where(source => source.Title.Contains("XBOX", StringComparison.OrdinalIgnoreCase)
                && source.Title.Contains("Chrome", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sources.Length != 1)
            {
                var studios = Process.GetProcessesByName("AccessRelayStudio").Where(p => p.MainWindowHandle != 0).ToArray();
                if (studios.Length != 1) throw new InvalidOperationException("No unique existing gameplay/Studio window is available. No window was opened or focused.");
                using var graphics = new CaptureD3D11();
                var item = CaptureD3D11.CreateItem(studios[0].MainWindowHandle);
                using var pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(graphics.Device,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                using var session = pool.CreateCaptureSession(item);
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new InvalidOperationException("This OS does not expose the update-interval property.");
                double original = session.MinUpdateInterval.TotalMilliseconds;
                session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
                var properties = new { defaultMinimumUpdateIntervalMs = original, requestedMinimumUpdateIntervalMs = 1,
                    effectiveMinimumUpdateIntervalMs = session.MinUpdateInterval.TotalMilliseconds,
                    liveGameplayMeasured = false, scope = "Property read/set/readback on the existing Studio window. Capture was not started; no window/input/controller action occurred. Gameplay window unavailable." };
                File.WriteAllText(Path.Combine(output, "interval-property-readback.json"), JsonSerializer.Serialize(properties, Json));
                Console.WriteLine(JsonSerializer.Serialize(properties, Json));
                return;
            }
            var baseline = Measure(sources[0], true, stop.Token);
            File.WriteAllText(Path.Combine(output, "capture-system-default.json"), JsonSerializer.Serialize(baseline, Json));
            Console.WriteLine(JsonSerializer.Serialize(baseline, Json));
            var changed = Measure(sources[0], false, stop.Token);
            File.WriteAllText(Path.Combine(output, "capture-low-latency.json"), JsonSerializer.Serialize(changed, Json));
            Console.WriteLine(JsonSerializer.Serialize(changed, Json));
        }
        finally { RoUninitialize(); }
    }

    private static object Measure(CaptureSource source, bool systemDefault, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var capture = new RemotePlayCapture();
        capture.Start(source.Hwnd, useSystemUpdateInterval: systemDefault);
        var receipts = new List<long>(); var compositor = new List<long>(); var readback = new List<double>();
        long began = Stopwatch.GetTimestamp(), measuredAt = 0; int frames = 0, changed = 0, identical = 0;
        CapturedVisionFrame? previous = null;
        try
        {
            while (Stopwatch.GetElapsedTime(began).TotalSeconds < 3.5)
            {
                stop.ThrowIfCancellationRequested();
                capture.FrameReady.WaitOne(30);
                if (!capture.TryAcquireLatest(out var frame) || frame is null) continue;
                if (Stopwatch.GetElapsedTime(began).TotalSeconds < .5) { frame.Dispose(); continue; }
                if (measuredAt == 0) measuredAt = Stopwatch.GetTimestamp();
                frames++; receipts.Add(frame.CaptureTicks);
                if (frame.CompositorTicks is { } ticks) compositor.Add(ticks);
                if (frame.CaptureReadbackMs is { } ms) readback.Add(ms);
                if (previous is not null)
                {
                    if (previous.Width != frame.Width || previous.Height != frame.Height) { frame.Dispose(); throw new InvalidOperationException("Source resized during the read-only comparison."); }
                    if (frame.Pixels.Span.SequenceEqual(previous.Pixels.Span)) identical++; else changed++;
                }
                previous?.Dispose(); previous = frame;
            }
        }
        finally { previous?.Dispose(); }
        if (frames < 2) throw new InvalidOperationException("The existing source did not deliver enough frames; no UI or controller was activated.");
        double seconds = (receipts[^1] - receipts[0]) / (double)Stopwatch.Frequency;
        object Stats(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length == 0 ? new { samples = 0, medianMs = 0d, p95ms = 0d, maxMs = 0d }
                : new { samples = sorted.Length, medianMs = sorted[(sorted.Length - 1) / 2],
                    p95ms = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)], maxMs = sorted[^1] };
        }
        IEnumerable<double> Gaps(List<long> ticks) => ticks.Zip(ticks.Skip(1), (a, b) => (b - a) * 1000d / Stopwatch.Frequency);
        return new { mode = systemDefault ? "system-default" : "requested-1ms", frames, seconds,
            deliveredFps = (frames - 1) / seconds, bitwiseChangedFps = changed / seconds, identicalAdjacentFrames = identical,
            receiptGaps = Stats(Gaps(receipts)), compositorGaps = Stats(Gaps(compositor)), readback = Stats(readback),
            pipeline = capture.Status.Pipeline, source = new { source.Title, capture.Status.Width, capture.Status.Height },
            scope = "Existing window capture only. No focus changes, UI input, controller actions, model inference or recording writer. Bitwise image comparison distinguishes repeated pixels from changed deliveries." };
    }

    private static object ResizeCheck(string take, CancellationToken stop)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(take, "session.json")));
        var stream = metadata.RootElement.GetProperty("captureEvidence").GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "scene");
        int storedWidth = stream.GetProperty("width").GetInt32(), storedHeight = stream.GetProperty("height").GetInt32();
        var stored = new byte[storedWidth * storedHeight * 4];
        using (var input = File.OpenRead(Path.Combine(take, "scene.bgra"))) input.ReadExactly(stored);
        const int width = 2560, height = 1440, tw = 1280, th = 720;
        var pixels = new byte[width * height * 4];
        new BgraCropResizer(width, height).Resize(stored, storedWidth, storedHeight, storedWidth * 4, pixels, new(0, 0, 1, 1));
        var original = new byte[tw * th * 4]; var optimized = new byte[original.Length];
        var resize = new BgraCropResizer(tw, th); var crop = new VisionCrop(0, 0, 1, 1);
        void Reference(ReadOnlySpan<byte> source, int sw, int sh, int stride, Span<byte> target, int dw, int dh, VisionCrop region)
        {
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(sh - 1, (int)((region.Top + (y + .5) / dh * region.Height) * sh));
                for (int x = 0; x < dw; x++)
                {
                    int sx = Math.Min(sw - 1, (int)((region.Left + (x + .5) / dw * region.Width) * sw));
                    source.Slice(sy * stride + sx * 4, 4).CopyTo(target.Slice((y * dw + x) * 4, 4));
                }
            }
        }
        // Vary both geometry and ROI; these bytes must match the existing preprocessing contract.
        var random = new Random(735);
        foreach (var (sw, sh, dw, dh, pad) in new[] { (7, 11, 13, 6, 8), (83, 49, 27, 53, 0), (64, 48, 64, 48, 0) })
        {
            var input = new byte[(sw * 4 + pad) * sh]; random.NextBytes(input);
            var actual = new byte[dw * dh * 4]; var expected = new byte[actual.Length]; var sampler = new BgraCropResizer(dw, dh);
            foreach (var box in new[] { new VisionCrop(0, 0, 1, 1), new(.17, .09, .61, .73), new(.25, .2, .5, .6), new(0, 0, 1, 1) })
            {
                Reference(input, sw, sh, sw * 4 + pad, expected, dw, dh, box);
                sampler.Resize(input, sw, sh, sw * 4 + pad, actual, box);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Optimized crop changed a source pixel.");
            }
        }
        Reference(pixels, width, height, width * 4, original, tw, th, crop);
        resize.Resize(pixels, width, height, width * 4, optimized, crop);
        if (!original.AsSpan().SequenceEqual(optimized)) throw new InvalidOperationException("1440p crop pixels changed.");
        double Time(Action action)
        {
            for (int i = 0; i < 5; i++) action();
            long at = Stopwatch.GetTimestamp();
            for (int i = 0; i < 40; i++) { stop.ThrowIfCancellationRequested(); action(); }
            return Stopwatch.GetElapsedTime(at).TotalMilliseconds / 40;
        }
        double before = Time(() => Reference(pixels, width, height, width * 4, original, tw, th, crop));
        double after = Time(() => resize.Resize(pixels, width, height, width * 4, optimized, crop));
        return new { scope = "1440p-sized input upsampled from a saved gameplay frame; CPU resize only, no disk writes or live capture.",
            pixelEqualityPassed = true, originalMeanMs = before, optimizedMeanMs = after, speedup = before / after };
    }

    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
}
