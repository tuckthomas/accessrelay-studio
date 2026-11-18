using System.Diagnostics;
using EliteScriptStudio.RemotePlay;
using EliteScriptStudio.Vision;
using static EliteScriptStudio.RemotePlay.GStreamerNative;

internal static class XboxColorConversionBenchmark
{
    private const int Width = 2560, Height = 1440, Frames = 240, Warmup = 30;
    private sealed record RunResult(int Received, List<double> ToMapped, List<double> ToPublished, byte[] ComparisonFrame);

    internal static void Run()
    {
        Initialize();
        string clip = Path.Combine(Path.GetTempPath(), $"AccessRelay-synthetic-color-{Guid.NewGuid():N}.mkv");
        string location = "\"" + clip.Replace('\\', '/') + "\"";
        try
        {
            // Both paths decode exactly the same timestamped H.264 access units. The encoder
            // has already stopped when timing starts. The card includes colored bars and text.
            CreateFixture(location);
            var cpu = new List<RunResult>();
            var gpu = new List<RunResult>();
            foreach (bool onGpu in new[] { false, true, true, false, false, true })
            {
                var result = Measure(location, onGpu);
                (onGpu ? gpu : cpu).Add(result);
                Console.WriteLine($"{(onGpu ? "GPU" : "CPU")} pass: {result.Received}/{Frames} full frames, " +
                    $"decoder output to published BGRA p50/p95 {Percentile(result.ToPublished, .5):F2}/{Percentile(result.ToPublished, .95):F2} ms.");
            }
            Report("CPU", cpu);
            Report("GPU", gpu);
            // No missing or replaced frame can shift the pair used for the image comparison.
            ComparePixels(cpu.First(result => result.Received == Frames).ComparisonFrame,
                gpu.First(result => result.Received == Frames).ComparisonFrame);
        }
        finally { File.Delete(clip); }
    }

    private static nint Parse(string description)
    {
        nint pipeline = gst_parse_launch(description, out var error);
        if (error == 0 && pipeline != 0) return pipeline;
        if (pipeline != 0) gst_object_unref(pipeline);
        throw Error(error, "The benchmark requires the Windows GStreamer D3D12 encoder, decoder and converters.");
    }

    private static void CreateFixture(string location)
    {
        nint pipeline = Parse($"videotestsrc num-buffers={Frames} pattern=smpte75 ! video/x-raw,width={Width},height={Height},framerate=60/1 " +
            "! textoverlay text=\"RHYTHM EXCELLENT\" font-desc=\"Sans 28\" ! videoconvert ! video/x-raw,format=NV12,colorimetry=bt709 " +
            $"! d3d12upload ! d3d12h264enc ! h264parse ! matroskamux ! filesink location={location}");
        nint bus = gst_element_get_bus(pipeline);
        try
        {
            if (gst_element_set_state(pipeline, 4) == 0) throw new IOException("Could not start the synthetic H.264 encoder.");
            nint message = gst_bus_timed_pop_filtered(bus, 30_000_000_000, 3);
            if (message == 0) throw new TimeoutException("The synthetic encoder did not reach EOS.");
            try
            {
                nint structure = gst_message_get_structure(message);
                if (structure != 0 && gst_structure_get_value(structure, "gerror") != 0)
                    throw new IOException("GStreamer reported an error encoding the synthetic test card.");
            }
            finally { gst_mini_object_unref(message); }
        }
        finally { gst_element_set_state(pipeline, 1); gst_object_unref(bus); gst_object_unref(pipeline); }
    }

    private static RunResult Measure(string location, bool onGpu)
    {
        string conversion = onGpu ? "d3d12convert ! video/x-raw(memory:D3D12Memory),format=BGRA ! d3d12download" : "d3d12download ! videoconvert";
        nint pipeline = Parse($"filesrc location={location} ! matroskademux ! h264parse ! clocksync ! d3d12h264dec " +
            "! video/x-raw(memory:D3D12Memory),format=NV12 ! identity name=afterdecode ! tee name=video " +
            "video. ! queue leaky=downstream max-size-buffers=1 ! video/x-raw(memory:D3D12Memory),format=NV12 ! fakesink sync=false async=false " +
            $"video. ! queue leaky=downstream max-size-buffers=1 ! {conversion} ! video/x-raw,format=BGRA ! appsink name=frames max-buffers=1 drop=true sync=false async=false");
        var stamps = new XboxStatsCollector();
        MessageHandler mark = (_, buffer, _) => stamps.Frame(buffer, decoded: true);
        nint marker = gst_bin_get_by_name(pipeline, "afterdecode"), sink = gst_bin_get_by_name(pipeline, "frames");
        int received = 0;
        List<double> mapped = [], published = [];
        byte[] comparison = [];
        using var pool = new LatestFrameBuffer();
        try
        {
            if (g_signal_connect_data(marker, "handoff", mark, 0, 0, 0) == 0 || gst_element_set_state(pipeline, 4) == 0)
                throw new IOException("Could not timestamp the decoded D3D12 surface.");
            while (received < Frames)
            {
                nint sample = gst_app_sink_try_pull_sample(sink, 5_000_000_000);
                if (sample == 0) break;
                try
                {
                    var caps = gst_caps_get_structure(gst_sample_get_caps(sample), 0);
                    if (gst_structure_get_int(caps, "width", out int width) == 0 || width != Width
                        || gst_structure_get_int(caps, "height", out int height) == 0 || height != Height
                        || Text(gst_structure_get_string(caps, "format")) != "BGRA")
                        throw new InvalidDataException("The benchmark did not retain the full BGRA picture.");
                    nint buffer = gst_sample_get_buffer(sample);
                    long start = stamps.DecoderTimestamp(buffer) ?? throw new InvalidDataException("A decoded frame lost its presentation timestamp.");
                    var timing = Publish(pool, buffer, start, ++received);
                    if (received > Warmup)
                    {
                        mapped.Add(Stopwatch.GetElapsedTime(start, timing.Mapped).TotalMilliseconds);
                        published.Add(Stopwatch.GetElapsedTime(start, timing.Published).TotalMilliseconds);
                    }
                    if (!pool.TryAcquireLatest(out var frame) || frame is null) throw new IOException("No full frame reached the consumer.");
                    using (frame)
                        if (received == Warmup / 2) comparison = frame.Pixels.ToArray();
                }
                finally { gst_mini_object_unref(sample); }
            }
            if (received < Frames * .95 || pool.DroppedFrames != 0) throw new IOException($"Invalid benchmark throughput: {received}/{Frames} samples.");
            return new(received, mapped, published, comparison);
        }
        finally
        {
            gst_element_set_state(pipeline, 1);
            gst_object_unref(sink); gst_object_unref(marker); gst_object_unref(pipeline);
            GC.KeepAlive(mark);
        }
    }

    private static (long Mapped, long Published) Publish(LatestFrameBuffer pool, nint buffer, long start, int sequence)
    {
        nint meta = gst_buffer_get_video_meta(buffer), pixels;
        Map map; int stride;
        if (meta != 0)
        {
            if (gst_video_meta_map(meta, 0, out map, out pixels, out stride, 1) == 0) throw new IOException("Could not map the BGRA plane.");
        }
        else
        {
            if (gst_buffer_map(buffer, out map, 1) == 0) throw new IOException("Could not map the BGRA buffer.");
            pixels = map.Data; stride = Width * 4;
        }
        try
        {
            long mapped = Stopwatch.GetTimestamp(), offset = pixels - map.Data;
            if (offset < 0 || stride < Width * 4 || (long)map.Size - offset < (long)(Height - 1) * stride + Width * 4)
                throw new InvalidDataException("Mapped frame has an invalid stride or truncated rows.");
            if (!pool.PublishMapped(pixels, (uint)stride, Width, Height, start, sequence, 1, mapped))
                throw new IOException("The Studio frame pool could not publish the measured frame.");
            return (mapped, Stopwatch.GetTimestamp());
        }
        finally
        {
            if (meta != 0) gst_video_meta_unmap(meta, 0, ref map);
            else gst_buffer_unmap(buffer, ref map);
        }
    }

    private static double Percentile(IEnumerable<double> samples, double fraction)
    {
        var ordered = samples.Order().ToArray();
        return ordered[(int)Math.Ceiling(fraction * ordered.Length) - 1];
    }

    private static void Report(string path, List<RunResult> runs)
    {
        var mapped = runs.SelectMany(run => run.ToMapped).ToArray();
        var published = runs.SelectMany(run => run.ToPublished).ToArray();
        Console.WriteLine($"{path} pooled: {runs.Sum(run => run.Received)}/{runs.Count * Frames} received, {published.Length} timed. " +
            $"Decoder output to CPU mapping p50/p95 {Percentile(mapped, .5):F2}/{Percentile(mapped, .95):F2} ms. " +
            $"Decoder output through Studio frame publication p50/p95 {Percentile(published, .5):F2}/{Percentile(published, .95):F2} ms.");
    }

    private static void ComparePixels(byte[] cpu, byte[] gpu)
    {
        if (cpu.Length != Width * Height * 4 || gpu.Length != cpu.Length) throw new InvalidDataException("Image comparison size mismatch.");
        int maximum = 0, overTwo = 0, alphaDifferences = 0;
        long total = 0;
        for (int i = 0; i < cpu.Length; i += 4)
        {
            if (cpu[i + 3] != gpu[i + 3]) alphaDifferences++;
            for (int channel = 0; channel < 3; channel++)
            {
                int difference = Math.Abs(cpu[i + channel] - gpu[i + channel]);
                total += difference;
                maximum = Math.Max(maximum, difference);
                if (difference > 2) overTwo++;
            }
        }
        int channels = Width * Height * 3;
        Console.WriteLine($"Matched full frame: RGB mean absolute difference {total / (double)channels:F3}/255, maximum {maximum}/255, " +
            $"{100d * overTwo / channels:F3}% of RGB channels differed by more than 2; {alphaDifferences} alpha differences.");
    }
}
