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
