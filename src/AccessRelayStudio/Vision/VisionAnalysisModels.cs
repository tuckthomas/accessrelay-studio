using System.Security.Cryptography;
using System.Text;
using EliteScriptStudio.Core;
using Microsoft.ML.OnnxRuntime;

namespace EliteScriptStudio.Vision;

/// <summary>Native ONNX person/pose and horizontal-text inference. Game adapters own association and region policy.</summary>
internal sealed class VisionAnalysisModels : IVisionAnalysisModels
{
    private readonly Runner _pose, _detector, _recognizer;
    private readonly string[] _characters;
    private bool[] _visited = [];
    private int[] _queue = [];
    public string ExecutionProvider { get; }
    internal VisionAnalysisModels(VisionCaptureModelSet models, VisionExecutionProvider provider, string? assetRoot = null)
    {
        ExecutionProvider = provider == VisionExecutionProvider.Cuda ? "CUDAExecutionProvider+CPU-operators" : "CPUExecutionProvider";
        if (models.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid capture model set.");
        string root = assetRoot is null ? Path.Combine(AppContext.BaseDirectory, "CaptureModels", models.Id) : Path.GetFullPath(assetRoot);
        string Verified(string name, string expected)
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 128 * 1024 * 1024)
                throw new InvalidDataException("The plugin's capture models are missing. Install its capture model assets.");
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(expected, StringComparison.Ordinal))
                throw new InvalidDataException("Capture model integrity check failed: " + name);
            return path;
        }
        if (provider == VisionExecutionProvider.Cuda) VisionModel.PrepareLocalCudaRuntime();
        _pose = new(Verified("pose.onnx", models.PoseSha256), provider);
        try
        {
            _detector = new(Verified("text-det.onnx", models.TextDetectorSha256), provider);
            try
            {
                _recognizer = new(Verified("text-rec.onnx", models.TextRecognizerSha256), provider);
                if (!_recognizer.Session.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? alphabet))
                    throw new InvalidDataException("The text recognizer has no bound alphabet.");
                _characters = ["", .. alphabet.TrimEnd('\r', '\n').Split('\n').Select(c => c.TrimEnd('\r')), " "];
            }
            catch { _recognizer?.Dispose(); _detector.Dispose(); throw; }
        }
        catch { _pose.Dispose(); throw; }
    }
    public VisionPersonPose[] DetectPeople(ReadOnlySpan<byte> bgra, int width, int height, int stride, VisionCrop? searchRegion = null)
    {
        var region = searchRegion ?? new(0, 0, 1, 1); region.Validate();
        const int size = 416; double ratio = Math.Min(size / (width * region.Width), size / (height * region.Height));
        int rw = Math.Clamp((int)(width * region.Width * ratio), 1, size), rh = Math.Clamp((int)(height * region.Height * ratio), 1, size);
        var input = _pose.Input(size, size); input.Fill(114);
        Resize(bgra, width, height, stride, region, rw, rh, input, size, size, 1, 0);
        using var result = _pose.Run();
        var outputs = result.ToArray();
        var det = outputs[0].GetTensorDataAsSpan<float>(); var joints = outputs[1].GetTensorDataAsSpan<float>();
        if (det.Length % 5 != 0 || joints.Length != det.Length / 5 * 17 * 3)
            throw new InvalidDataException("The person/pose outputs do not match the reviewed 17-keypoint contract.");
        var people = new List<VisionPersonPose>();
        for (int i = 0; i < det.Length / 5; i++)
        {
            float score = det[i * 5 + 4]; if (!float.IsFinite(score) || score < (searchRegion is null ? .45 : .25)) continue;
            double left = Math.Clamp(region.Left + det[i * 5] / (ratio * width), 0, 1), top = Math.Clamp(region.Top + det[i * 5 + 1] / (ratio * height), 0, 1);
            double right = Math.Clamp(region.Left + det[i * 5 + 2] / (ratio * width), 0, 1), bottom = Math.Clamp(region.Top + det[i * 5 + 3] / (ratio * height), 0, 1);
            if (right - left < .01 || bottom - top < .025) continue;
            var pose = new VisionKeypoint[17];
            for (int k = 0; k < pose.Length; k++)
            {
                int p = (i * 17 + k) * 3;
                pose[k] = new(region.Left + joints[p] / (ratio * width), region.Top + joints[p + 1] / (ratio * height), joints[p + 2]);
            }
            if (pose.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Confidence))) continue;
            people.Add(new(new(left, top, right - left, bottom - top), score, pose));
        }
        var kept = new List<VisionPersonPose>();
        foreach (var person in people.OrderByDescending(p => p.Confidence))
        { if (kept.All(p => IntersectionOverUnion(p.Bounds, person.Bounds) < .45)) kept.Add(person); if (kept.Count == 40) break; }
        return kept.ToArray();
    }
    public VisionTextRegion[] ReadText(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        double scale = Math.Min(1, 1536d / Math.Max(width, height));
        int tw = Math.Max(32, (int)Math.Round(width * scale / 32) * 32), th = Math.Max(32, (int)Math.Round(height * scale / 32) * 32);
        var input = _detector.Input(tw, th);
        Resize(bgra, width, height, stride, new(0, 0, 1, 1), tw, th, input, tw, th, 1 / 127.5f, -1);
        using var result = _detector.Run();
        var detectorOutput = result.First();
        var shape = detectorOutput.GetTensorTypeAndShape().Shape;
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 1 || shape[2] > 1536 || shape[3] > 1536)
            throw new InvalidDataException("Unsupported text detector output.");
        int mh = (int)shape[2], mw = (int)shape[3];
        var values = detectorOutput.GetTensorDataAsSpan<float>();
        if (_visited.Length != mw * mh) { _visited = new bool[mw * mh]; _queue = new int[mw * mh]; }
        Array.Clear(_visited);
        var visited = _visited; var queue = _queue;
        ReadOnlySpan<int> offsets = [-1, 1, -mw, mw]; var boxes = new List<(VisionCrop Box, double Score)>();
        for (int start = 0; start < values.Length; start++)
        {
            if (visited[start] || values[start] < .3) continue;
            int head = 0, tail = 1, minX = start % mw, maxX = minX, minY = start / mw, maxY = minY; double score = 0;
            queue[0] = start; visited[start] = true;
            while (head < tail)
            {
                int index = queue[head++], x = index % mw, y = index / mw; score += values[index];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                foreach (int offset in offsets)
                {
                    int n = index + offset;
                    if (n < 0 || n >= values.Length || (Math.Abs(offset) == 1 && n / mw != y) || visited[n] || values[n] < .3) continue;
                    visited[n] = true; queue[tail++] = n;
                }
            }
            if (tail < 8 || score / tail < .55 || maxX - minX < 4 || maxY - minY < 2) continue;
            double margin = Math.Max(2, (maxY - minY) * .55);
            double l = Math.Max(0, minX - margin) / mw, t = Math.Max(0, minY - margin) / mh;
            double r = Math.Min(mw, maxX + 1 + margin) / mw, b = Math.Min(mh, maxY + 1 + margin) / mh;
            if (r - l < (b - t) * .7 || b - t > .12) continue;
            boxes.Add((new(l, t, r - l, b - t), score / tail));
        }
        var lines = new List<VisionTextRegion>();
        foreach (var (box, _) in boxes.OrderByDescending(b => b.Box.Width).Take(64))
        {
            const int recWidth = 320, recHeight = 48;
            int resized = Math.Min(recWidth, Math.Max(1, (int)Math.Ceiling(recHeight * box.Width * width / (box.Height * height))));
            var recInput = _recognizer.Input(recWidth, recHeight); recInput.Clear();
            Resize(bgra, width, height, stride, box, resized, recHeight, recInput, recWidth, recHeight, 1 / 127.5f, -1);
            using var recognized = _recognizer.Run();
            var recognizedOutput = recognized.First();
            var dims = recognizedOutput.GetTensorTypeAndShape().Shape;
            if (dims.Length != 3 || dims[0] != 1 || dims[2] != _characters.Length) throw new InvalidDataException("Text alphabet/output mismatch.");
            var probabilities = recognizedOutput.GetTensorDataAsSpan<float>();
            int classes = _characters.Length, previous = -1, count = 0; double confidence = 0; var text = new StringBuilder();
            for (int step = 0; step < probabilities.Length / classes; step++)
            {
                int best = 0; float value = probabilities[step * classes];
                for (int c = 1; c < classes; c++) if (probabilities[step * classes + c] > value) { best = c; value = probabilities[step * classes + c]; }
                if (best != 0 && best != previous) { text.Append(_characters[best]); confidence += value; count++; }
                previous = best;
            }
            if (count > 0 && confidence / count >= .65) lines.Add(new(text.ToString().Trim(), confidence / count, box));
        }
        return lines.ToArray();
    }
    private static void Resize(ReadOnlySpan<byte> bgra, int width, int height, int stride, VisionCrop crop, int rw, int rh,
        Span<float> target, int tw, int th, float scale, float offset)
    {
        for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
        {
            double sx = (crop.Left + (x + .5) / rw * crop.Width) * width - .5, sy = (crop.Top + (y + .5) / rh * crop.Height) * height - .5;
            int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy); float dx = (float)(sx - ix), dy = (float)(sy - iy);
            int x0 = Math.Clamp(ix, 0, width - 1), x1 = Math.Clamp(ix + 1, 0, width - 1), y0 = Math.Clamp(iy, 0, height - 1), y1 = Math.Clamp(iy + 1, 0, height - 1);
            for (int c = 0; c < 3; c++)
            {
                float a = bgra[y0 * stride + x0 * 4 + c] * (1 - dx) + bgra[y0 * stride + x1 * 4 + c] * dx;
                float b = bgra[y1 * stride + x0 * 4 + c] * (1 - dx) + bgra[y1 * stride + x1 * 4 + c] * dx;
                target[c * tw * th + y * tw + x] = (a * (1 - dy) + b * dy) * scale + offset;
            }
        }
    }
    private static double IntersectionOverUnion(VisionCrop a, VisionCrop b)
    {
        double area = Math.Max(0, Math.Min(a.Left + a.Width, b.Left + b.Width) - Math.Max(a.Left, b.Left))
            * Math.Max(0, Math.Min(a.Top + a.Height, b.Top + b.Height) - Math.Max(a.Top, b.Top));
        return area / Math.Max(1e-10, a.Width * a.Height + b.Width * b.Height - area);
    }
    private sealed class Runner : IDisposable
    {
        internal InferenceSession Session { get; }
        private readonly RunOptions _run = new();
        private readonly string[] _inputs, _outputs;
        private OrtValue? _input;
        private float[] _data = [];
        private int _width, _height;
        internal Runner(string path, VisionExecutionProvider provider)
        {
            using var options = CaptureOptions(provider); Session = new(path, options);
            _inputs = Session.InputNames.ToArray(); _outputs = Session.OutputNames.ToArray();
            if (_inputs.Length != 1) throw new InvalidDataException("The capture model requires one input.");
        }
        private static SessionOptions CaptureOptions(VisionExecutionProvider provider)
        {
            if (provider == VisionExecutionProvider.Cpu) return VisionModel.CreateSessionOptions(provider);
            if (!OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider")) throw new InvalidOperationException("CUDA capture detection is unavailable. Select CPU explicitly.");
            using var cuda = new OrtCUDAProviderOptions();
            cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0", ["gpu_mem_limit"] = "1073741824", ["cudnn_conv_algo_search"] = "HEURISTIC" });
            // Detection exports contain shape/NMS operators assigned to CPU by ORT.
            // This explicit mixed-operator policy applies to acquisition, not release-model acceptance.
            var options = SessionOptions.MakeSessionOptionWithCudaProvider(cuda);
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            options.IntraOpNumThreads = 2; options.InterOpNumThreads = 1; options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            return options;
        }
        internal Span<float> Input(int width, int height)
        {
            if (_input is null || width != _width || height != _height)
            {
                _input?.Dispose(); _data = GC.AllocateUninitializedArray<float>(3 * width * height, pinned: true);
                _input = OrtValue.CreateTensorValueFromMemory(_data, [1, 3, height, width]); _width = width; _height = height;
            }
            return _data;
        }
        internal IDisposableReadOnlyCollection<OrtValue> Run() => Session.Run(_run, _inputs, [_input!], _outputs);
        internal void Stop() { try { _run.Terminate = true; } catch (ObjectDisposedException) { } }
        public void Dispose() { _input?.Dispose(); _run.Dispose(); Session.Dispose(); }
    }
    public void RequestStop() { _pose.Stop(); _detector.Stop(); _recognizer.Stop(); }
    public void Dispose() { _recognizer.Dispose(); _detector.Dispose(); _pose.Dispose(); }
}
