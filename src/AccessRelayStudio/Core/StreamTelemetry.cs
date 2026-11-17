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

public sealed record StreamTelemetryAssessment(string State, string Message, bool CanAdapt,
    double AdditionalUncertaintyMs, StreamTelemetryReading? Latest, int Samples);

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
    private readonly List<StreamTelemetryReading> _readings = [];
    internal void Reset() => _readings.Clear();
    internal void Add(StreamTelemetryReading reading)
    {
        if (reading.CaptureTimestamp <= 0 || _readings.Count > 0 && reading.CaptureTimestamp <= _readings[^1].CaptureTimestamp
            || !double.IsFinite(reading.PingMs) || reading.PingMs is < 0 or > 2000
            || !double.IsFinite(reading.JitterMs) || reading.JitterMs is < 0 or > 1000
            || !double.IsFinite(reading.DecodeMs) || reading.DecodeMs is < 0 or > 1000
            || !double.IsFinite(reading.Fps) || reading.Fps is < 1 or > 240
            || reading.PacketLossPercent is { } loss && (!double.IsFinite(loss) || loss is < 0 or > 100)) return;
        _readings.Add(reading);
        if (_readings.Count > 64) _readings.RemoveAt(0);
    }
    internal StreamTelemetryAssessment Assess(long from, long now, double budgetMs, bool enabled)
    {
        if (!enabled) return new("disabled", "Stream statistics monitoring is off.", true, 0, null, 0);
        var recent = _readings.Where(r => r.CaptureTimestamp <= now && now - r.CaptureTimestamp <= frequency * 3).ToArray();
        if (recent.Length == 0) return new("unavailable", "Enable the Xbox stream statistics overlay. No fresh readable statistics; automatic tuning is paused.", false, 0, null, 0);
        var samples = _readings.Where(r => r.CaptureTimestamp <= now && r.CaptureTimestamp >= from - frequency * 2).ToArray();
        if (samples.Length == 0) samples = recent;
        double Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return a[a.Length / 2]; }
        var baseline = _readings.Where(r => r.CaptureTimestamp <= now).TakeLast(20).ToArray();
        double ping = Median(baseline.Select(r => r.PingMs)), decode = Median(baseline.Select(r => r.DecodeMs));
        double nominalFps = Median(baseline.Select(r => r.Fps));
        double uncertainty = samples.Max(r => r.JitterMs + Math.Abs(r.PingMs - ping) + Math.Abs(r.DecodeMs - decode));
        bool gap = false;
        for (int i = 1; i < samples.Length; i++) gap |= samples[i].CaptureTimestamp - samples[i - 1].CaptureTimestamp > frequency * 3;
        bool unstable = gap || uncertainty > budgetMs || samples.Any(r => r.PacketLossPercent > 0 || r.DecodeMs > Math.Max(12, budgetMs)
            || r.Fps < Math.Max(25, nominalFps * .8));
        string state = unstable ? "unstable" : recent.Length < 2 ? "warmingUp" : "stable";
        return new(state, unstable ? "Stream jitter, decode delay, frame rate or packet loss exceeded the timing budget. Automatic tuning is paused."
            : recent.Length < 2 ? "Confirming stream statistics before automatic tuning."
            : "Stream statistics are stable. Configured timing compensation is retained; displayed ping/latency is not an end-to-end measurement.",
            !unstable && recent.Length >= 2, Math.Min(1000, uncertainty), recent[^1], samples.Length);
    }
}
