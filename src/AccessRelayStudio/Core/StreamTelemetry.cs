using System.Globalization;
using System.Text.RegularExpressions;

namespace EliteScriptStudio.Core;

public sealed record StreamTelemetrySettings
{
    public bool Enabled { get; init; } = true;
    public VisionCrop Region { get; init; } = new(.45, 0, .55, .22);
    public void Validate() => Region.Validate();
}

public sealed record StreamTelemetryReading(long CaptureTimestamp, double PingMs, double JitterMs,
    double DecodeMs, double Fps, double? PacketLossPercent, string Source, string RawText);

public sealed record DirectStreamMetrics(long CaptureTimestamp, double? RttMs, string? RttSource, double? JitterMs,
    double? DecodeMs, double? Fps, double? PacketLossPercent, double? JitterBufferMs, long? PacketsLost,
    long? FramesDropped, int Width, int Height)
{
    public string? VideoDecoder { get; init; }
    public ControllerHandoffStats? ControllerWrites { get; init; }
}

public sealed record ControllerHandoffStats(long Accepted, long Rejected, int Samples, double MedianMs, double P95Ms);

public sealed record StreamTelemetryAssessment(string State, string Message, bool CanAdapt,
    double AdditionalUncertaintyMs, StreamTelemetryReading? Latest, int Samples)
{
    public DirectStreamMetrics? Direct { get; init; }
}

/// <summary>Label-bound parsing; unavailable values never become zero. Ping/latency is the displayed value; its label is not proof of end-to-end delay.</summary>
internal static class StreamTelemetryParser
{
    internal static StreamTelemetryReading? Parse(string text, long capturedAt, string source = "Xbox overlay / Windows OCR")
    {
        if (text.Length > 8192 || capturedAt <= 0) return null;
        double? Value(string label, bool percent = false)
        {
            string number = @"(?<n>\d{1,4}(?:[.,]\d{1,3})?)";
            string unit = percent ? @"[ \t]*%" : @"[ \t]*(?:ms\b)?";
            var matches = Regex.Matches(text, @"\b(?:" + label + @")[ \t]*[:=]?[ \t]*" + number + unit,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
            // Prefer labelled values. A second pattern supports "60 FPS" and "2 ms Jitter".
            if (matches.Count == 0)
                matches = Regex.Matches(text, number + unit + @"[ \t]*(?:" + label + @")\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
            var values = matches.Select(m => double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture)).Distinct().ToArray();
            return values.Length == 1 ? values[0] : null;
        }
        double? ping = Value("PING|LATENCY|RTT"), jitter = Value("JITTER"), decode = Value("DECODE(?: TIME)?"), fps = Value("FPS");
        double? loss = Value("PACKET LOSS|PACKETS LOST|LOSS", percent: true);
        if (ping is not (>= 0 and <= 2000) || jitter is not (>= 0 and <= 1000)
            || decode is not (>= 0 and <= 1000) || fps is not (>= 1 and <= 240) || loss is < 0 or > 100) return null;
        return new(capturedAt, ping.Value, jitter.Value, decode.Value, fps.Value, loss, source, text);
    }
}

/// <summary>Bounded per-session history. Runtime owns serialization. Telemetry gates learning;
/// RTT is never silently substituted for video/input delay or added to a calibrated offset.</summary>
internal sealed class StreamTelemetryWindow(long frequency)
{
    private sealed record Sample(long Timestamp, double? Rtt, string? RttSource, double? Jitter, double? Decode, double? Fps,
        double? Loss, StreamTelemetryReading? Overlay, DirectStreamMetrics? Direct);

    private readonly List<Sample> _readings = [];
    internal void Reset() => _readings.Clear();

    internal void Add(StreamTelemetryReading r) => Add(new Sample(r.CaptureTimestamp, r.PingMs, r.Source, r.JitterMs,
        r.DecodeMs, r.Fps, r.PacketLossPercent, r, null));
    internal void Add(DirectStreamMetrics r) => Add(new Sample(r.CaptureTimestamp, r.RttMs, r.RttSource, r.JitterMs,
        r.DecodeMs, r.Fps, r.PacketLossPercent, null, r));

    private void Add(Sample sample)
    {
        static bool Valid(double? value, double maximum) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= maximum;
        if (sample.Timestamp <= 0 || _readings.Count > 0 && sample.Timestamp <= _readings[^1].Timestamp
            || !Valid(sample.Rtt, 2000) || !Valid(sample.Jitter, 1000) || !Valid(sample.Decode, 1000)
            || !Valid(sample.Fps, 240) || sample.Overlay is not null && sample.Fps < 1 || !Valid(sample.Loss, 100)) return;
        _readings.Add(sample);
        if (_readings.Count > 64) _readings.RemoveAt(0);
    }

    internal StreamTelemetryAssessment Assess(long from, long now, double budgetMs, bool enabled)
    {
        if (!enabled) return new("disabled", "Stream statistics monitoring is off.", true, 0, null, 0);
        var history = _readings.Where(r => r.Timestamp <= now).ToArray();
        var recent = history.Where(r => now - r.Timestamp <= frequency * 3).ToArray();
        if (recent.Length == 0) return new("unavailable", "Waiting for fresh stream statistics. Automatic tuning is paused.", false, 0, null, 0);

        var latest = recent[^1];
        var samples = history.Where(r => r.Timestamp >= from - frequency * 2).ToArray();
        if (samples.Length == 0) samples = recent;
        StreamTelemetryAssessment Result(string state, string message, bool adapt, double uncertainty) =>
            new(state, message, adapt, Math.Min(1000, uncertainty), latest.Overlay, samples.Length) { Direct = latest.Direct };
        if (samples.Any(r => !r.Jitter.HasValue || !r.Decode.HasValue || !r.Fps.HasValue))
            return Result("unavailable", "Waiting for jitter, decoder time and frame rate. Automatic tuning is paused.", false, 0);

        static double Median(IEnumerable<double?> values)
        {
            var a = values.OfType<double>().Order().ToArray();
            return a[a.Length / 2];
        }
        var baseline = history.Where(r => r.Decode.HasValue && r.Fps.HasValue).TakeLast(20).ToArray();
        double decode = Median(baseline.Select(r => r.Decode)), nominalFps = Median(baseline.Select(r => r.Fps));
        var rttBaseline = history.Where(r => r.Rtt.HasValue && r.RttSource is not null).TakeLast(20)
            .GroupBy(r => r.RttSource!).ToDictionary(g => g.Key, g => Median(g.Select(r => r.Rtt)));
        double uncertainty = samples.Max(r => r.Jitter!.Value + Math.Abs(r.Decode!.Value - decode) +
            (r.Rtt is double rtt && r.RttSource is string source && rttBaseline.TryGetValue(source, out double median) ? Math.Abs(rtt - median) : 0));
        bool gap = false;
        for (int i = 1; i < samples.Length; i++) gap |= samples[i].Timestamp - samples[i - 1].Timestamp > frequency * 3;
        bool unstable = gap || uncertainty > budgetMs || samples.Any(r => r.Loss > 0 || r.Decode > Math.Max(12, budgetMs)
            || r.Fps < Math.Max(25, nominalFps * .8));
        if (unstable) return Result("unstable", "Stream statistics exceeded the timing budget. Automatic tuning is paused.", false, uncertainty);
        return recent.Length < 2 ? Result("warmingUp", "Confirming stream statistics before automatic tuning.", false, uncertainty)
            : Result("stable", "Stream statistics are stable.", true, uncertainty);
    }
}
