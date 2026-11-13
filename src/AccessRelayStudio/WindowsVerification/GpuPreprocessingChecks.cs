using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;
using static EliteScriptStudio.Vision.D3D11ComputeNative;

internal static class GpuPreprocessingChecks
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual D3D11 shader checks require Windows; no portable substitute is a pass.");
        using var graphics = new CaptureD3D11(software: true);
        int cases = 0; double maximumError = 0;
        foreach (var source in new[] { (2, 3), (53, 41), (337, 193) })
        foreach (var target in new[] { (1, 1), (37, 43), (160, 160) })
        foreach (var crop in new[] { new VisionCrop(0, 0, 1, 1), new(.137, .213, .681, .537) })
        {
            var bytes = Image(source.Item1, source.Item2);
            float[] mean = [.13f, -.21f, .37f], standardDeviation = [.31f, .53f, 1.71f];
            var plan = new RgbSamplingPlan(source.Item1, source.Item2, crop, target.Item1, target.Item2, mean, standardDeviation);
            nint texture = 0, view = 0;
            try
            {
                texture = Texture(graphics, source.Item1, source.Item2, bytes);
                Marshal.ThrowExceptionForHR(Method<CreateView>(graphics.NativeDevice, 7)(graphics.NativeDevice, texture, 0, out view));
                using var shader = new D3D11RgbPreprocessor(graphics, plan);
                var result = shader.Process(view);
                Require(result.PlayerChanged && result.SourceFingerprint == Fingerprint(bytes, source.Item1, source.Item2), "GPU source fingerprint or initial content identity differs.");
                var expected = new float[plan.FrameLength]; var actual = new float[plan.FrameLength];
                FramePreprocessor.FillRgb(bytes, source.Item1, source.Item2, source.Item1 * 4, crop, target.Item1, target.Item2, mean, standardDeviation, expected);
                shader.ReadCurrent(actual);
                for (int i = 0; i < expected.Length; i++)
                {
                    double error = Math.Abs(expected[i] - actual[i]); maximumError = Math.Max(maximumError, error);
                    Require(float.IsFinite(actual[i]) && error <= 2e-6 + 2e-6 * Math.Abs(expected[i]), $"Shader parity differs at {source}/{target}/{crop}, element {i}: {expected[i]} vs {actual[i]}.");
                }
                Require(!shader.Process(view).PlayerChanged, "Unchanged selected-player pixels did not freeze on the GPU.");
                shader.ResetContent(); Require(shader.Process(view).PlayerChanged, "GPU source reset retained old content identity.");
                cases++;
            }
            finally { Release(ref view); Release(ref texture); }
        }
        Console.WriteLine($"SUCCESS actual D3D11 WARP RGB shader: {cases} gradient/crop/shape/normalization cases; maximum absolute error {maximumError:G9}; exact source fingerprints and player freeze/reset. WARP is not hardware/performance evidence.");
        LeaseChecks();
        OutsidePlayerFreeze(graphics);
    }

    private static void OutsidePlayerFreeze(CaptureD3D11 graphics)
    {
        const int width = 64, height = 32;
        var bytes = Image(width, height); nint texture = 0, view = 0;
        try
        {
            texture = Texture(graphics, width, height, bytes);
            Marshal.ThrowExceptionForHR(Method<CreateView>(graphics.NativeDevice, 7)(graphics.NativeDevice, texture, 0, out view));
            using var shader = new D3D11RgbPreprocessor(graphics, new(width, height, new(0, 0, .5, 1), 32, 32, [0, 0, 0], [1, 1, 1]));
            var first = shader.Process(view);
            for (int y = 0; y < height; y++) for (int x = width / 2; x < width; x++) bytes[(y * width + x) * 4] ^= 255;
            UpdateTexture(graphics, texture, bytes, width * 4);
            var outside = shader.Process(view);
            Require(!outside.PlayerChanged && outside.SourceFingerprint != first.SourceFingerprint, "Animated outside pixels concealed the GPU player's freeze.");
            for (int y = 0; y < height; y++) for (int x = 0; x < width / 2; x++) bytes[(y * width + x) * 4 + 2] ^= 255;
            UpdateTexture(graphics, texture, bytes, width * 4);
            Require(shader.Process(view).PlayerChanged, "GPU player reacquisition did not detect changed player pixels.");
        }
        finally { Release(ref view); Release(ref texture); }
        Console.WriteLine("SUCCESS actual GPU selected-player freeze remains independent of changing outside overlays.");
    }

    private static void LeaseChecks()
    {
        using var graphics = new CaptureD3D11(software: true);
        using var pool = new GpuCaptureBuffer(graphics);
        nint source = 0, resized = 0;
        CapturedVisionFrame? first = null, second = null, third = null, last = null;
        try
        {
            var a = Image(4, 3); var b = a.Select(value => (byte)(value ^ 127)).ToArray(); var c = a.Select(value => (byte)(value ^ 255)).ToArray();
            source = Texture(graphics, 4, 3, a);
            Require(pool.Publish(source, 4, 3, 101, 1, 7) && pool.TryAcquireLatest(out first), "GPU lease A failed.");
            UpdateTexture(graphics, source, b, 16);
            Require(pool.Publish(source, 4, 3, 102, 2, 7) && pool.TryAcquireLatest(out second), "GPU lease B failed.");
            UpdateTexture(graphics, source, c, 16);
            Require(pool.Publish(source, 4, 3, 103, 3, 7) && pool.TryAcquireLatest(out third), "GPU lease C failed.");
            Require(first!.OptionalReadbackBytes == 0 && second!.OptionalReadbackBytes == 0 && third!.OptionalReadbackBytes == 0, "GPU publication downloaded an image.");
            Require(!pool.Publish(source, 4, 3, 104, 4, 7), "GPU publisher overwrote an outstanding consumer.");
            pool.Clear();
            ((GpuFrameSurface)first.Surface!).ReportFingerprint?.Invoke(123);
            Require(pool.ContentChangedTicks == 0, "An old GPU lease published freshness after a source clear.");
            Require(first.Pixels.Span.SequenceEqual(a) && second!.Pixels.Span.SequenceEqual(b) && third!.Pixels.Span.SequenceEqual(c), "GPU in-flight textures were overwritten.");
            Require(first.OptionalReadbackBytes == a.Length, "Explicit lazy readback was not accounted.");
            first.Dispose(); first.Dispose(); first = null;
            var small = Image(2, 2); resized = Texture(graphics, 2, 2, small);
            Require(pool.Publish(resized, 2, 2, 105, 5, 8) && pool.Publish(resized, 2, 2, 106, 6, 8), "GPU resize/pending replacement failed.");
            Require(pool.TryAcquireLatest(out last) && last!.Sequence == 6 && last.Width == 2 && last.SourceGeneration == 8, "GPU newest-frame metadata differs.");
            pool.Dispose(); graphics.Dispose();
            Require(last!.Pixels.Span.SequenceEqual(small) && second!.Pixels.Span.SequenceEqual(b), "Capture shutdown invalidated a GPU frame lease.");
            Require(!pool.TryAcquireLatest(out _) && !pool.Publish(source, 4, 3, 107, 7, 8), "Disposed GPU publication remained active.");
            second!.Dispose(); second = null; third!.Dispose(); third = null; last.Dispose();
            try { _ = last.Pixels; throw new Exception("Disposed GPU frame remained readable."); } catch (ObjectDisposedException) { }
            last = null;
            Require(graphics.NativeDevice == 0 && graphics.NativeContext == 0, "GPU owner references leaked after the last consumer.");
        }
        finally { first?.Dispose(); second?.Dispose(); third?.Dispose(); last?.Dispose(); Release(ref resized); Release(ref source); }
        Console.WriteLine("SUCCESS actual WARP GPU leases: bounded slots, no automatic image download, replacement, resize, lazy readback, source generations, shutdown retention and release.");
    }

    internal static void UpdateTexture(CaptureD3D11 graphics, nint texture, byte[] bytes, int stride)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { lock (graphics.Sync) Method<Update>(graphics.NativeContext, 48)(graphics.NativeContext, texture, 0, 0, pinned.AddrOfPinnedObject(), (uint)stride, 0); }
        finally { pinned.Free(); }
    }
    internal static byte[] Image(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            bytes[offset] = (byte)((x * 11 + y * 17) % 256); bytes[offset + 1] = (byte)((x * 29 + y * 7) % 256);
            bytes[offset + 2] = (byte)((x * 3 + y * 43) % 256); bytes[offset + 3] = (byte)((x + y) % 256);
        }
        return bytes;
    }
    internal static nint Texture(CaptureD3D11 graphics, int width, int height, byte[] bytes)
    {
        var description = new TextureDescription { Width = (uint)width, Height = (uint)height, Mips = 1, ArraySize = 1, Format = 87, Samples = 1, BindFlags = 8 };
        nint data = Marshal.AllocHGlobal(Marshal.SizeOf<SubresourceData>()); var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(new SubresourceData { Data = pinned.AddrOfPinnedObject(), RowPitch = (uint)(width * 4) }, data, false);
            Marshal.ThrowExceptionForHR(Method<CreateTexture>(graphics.NativeDevice, 5)(graphics.NativeDevice, ref description, data, out var texture));
            return texture;
        }
        finally { pinned.Free(); Marshal.FreeHGlobal(data); }
    }
    private static ulong Fingerprint(byte[] bytes, int width, int height)
    {
        ulong hash = 14695981039346656037;
        for (int y = 0; y < height; y += Math.Max(1, height / 36))
            for (int x = 0; x < width; x += Math.Max(1, width / 64))
                for (int c = 0; c < 3; c++) hash = unchecked((hash ^ bytes[(y * width + x) * 4 + c]) * 1099511628211);
        return hash;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
