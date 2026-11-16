using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class DiagnosticRecordingChecks
{
    internal static void Run(string parent)
    {
        Directory.CreateDirectory(parent);
        var physical = new ControllerState { RightY = short.MinValue };
        var signal = new VisionSignalObservation(41, 123, true, new(.2, .3, .05, .2), .57, 1,
            -.001, null, .92, 10, "Fill velocity is not yet reliable.", 8, 1.5, 16, 12);
        byte[] pixels = Enumerable.Range(0, 16 * 12 * 4).Select(i => (byte)i).ToArray();
        byte[] expected = pixels.ToArray();
        string directory;
        using (var recorder = new VisionDiagnosticRecorder(parent, new { settings = VisionSettings.Default, plugin = "fixture" }))
        {
            directory = recorder.DirectoryPath;
            using var frame = new CapturedVisionFrame(pixels, 16, 12, 123, 41, 7, () => { }, compositorTicks: 120);
            recorder.Capture(frame, 150, 2, signal, new(physical, 122, 1, 2, 100, true), physical,
                default, null, true, false, false, null);
            Array.Fill(pixels, (byte)0); // The capture lease may be reused immediately after Capture returns.
            recorder.Stop();
            Check(recorder.Completion.Wait(5000), "Diagnostic writer did not finalize after Stop.");
            Check(recorder.Snapshot is { IsFinalized: true, Failed: false, StoredFrames: 1 }, "Recording did not finalize successfully.");
        }
        using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "diagnostic.json"))))
        {
            var root = report.RootElement; var frame = root.GetProperty("frames")[0];
            Check(root.GetProperty("finalized").GetBoolean(), "Saved report is still marked incomplete.");
            Check(frame.GetProperty("sequence").GetInt64() == 41 && frame.GetProperty("compositorTicks").GetInt64() == 120
                && frame.GetProperty("signal").GetProperty("progress").GetDouble() == .57
                && frame.GetProperty("physicalContext").GetProperty("physical").GetProperty("rightY").GetInt16() == short.MinValue,
                "Image/detection/input/timestamp alignment was lost.");
            using var gzip = new GZipStream(File.OpenRead(Path.Combine(directory, frame.GetProperty("file").GetString()!)), CompressionMode.Decompress);
            using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
            Check(decoded.ToArray().SequenceEqual(expected), "Diagnostic pixels were resized, changed, or reused before writing.");
        }

        // Block storage, not the capture caller. Metadata must survive every skipped image.
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using (var recorder = new VisionDiagnosticRecorder(parent, new { fixture = "slow-disk" }, _ =>
        { entered.Set(); if (!release.Wait(5000)) throw new IOException("Fixture timed out."); return new MemoryStream(); }))
        {
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    using var frame = new CapturedVisionFrame(expected, 16, 12, 200 + i, i + 1, 7, () => { });
                    recorder.Capture(frame, 250 + i, 1, signal with { Sequence = i + 1 }, null, null, default, null, true, false, false, null);
                    if (i == 0) Check(entered.Wait(5000), "Slow-disk fixture did not reach the writer.");
                }
                Check(recorder.Snapshot.ObservedFrames == 20 && recorder.Snapshot.DroppedFrames == 17,
                    "Slow disk did not preserve the trace while bounding pending images to three.");
                recorder.Stop();
            }
            finally { release.Set(); }
            Check(recorder.Completion.Wait(5000), "Slow-disk queue did not drain.");
            Check(recorder.Snapshot.StoredFrames == 3 && recorder.Snapshot.DroppedFrames == 17, "Stored/skipped image counts changed after draining.");
        }
        using (var recorder = new VisionDiagnosticRecorder(parent, new { fixture = "disk-error" }, _ => throw new IOException("Injected disk failure")))
        {
            using var frame = new CapturedVisionFrame(expected, 16, 12, 1, 1, 7, () => { });
            recorder.Capture(frame, 2, 1, signal, null, null, default, null, true, false, false, null);
            Check(recorder.Completion.Wait(5000), "Disk failure left recording active.");
            Check(recorder.Snapshot is { Failed: true, IsFinalized: true, IsRecording: false, DroppedFrames: 1 }, "Disk failure was reported as successful.");
        }
        using (var recorder = new VisionDiagnosticRecorder(parent, new { fixture = "timeout" }, duration: TimeSpan.FromMilliseconds(40)))
        {
            Check(recorder.Completion.Wait(5000), "Idle recording did not stop at its duration limit.");
            Check(recorder.Snapshot is { IsRecording: false, IsFinalized: true, StoredFrames: 0 }, "Empty recording did not finalize.");
        }
        using (var recorder = new VisionDiagnosticRecorder(parent, new { fixture = "size-limit" }, byteLimit: 1))
        {
            using var frame = new CapturedVisionFrame(expected, 16, 12, 1, 1, 7, () => { });
            recorder.Capture(frame, 2, 1, signal, null, null, default, null, true, false, false, null);
            Check(recorder.Completion.Wait(5000), "Compressed-size limit did not stop recording.");
            Check(recorder.Snapshot is { IsFinalized: true, IsRecording: false, StoredFrames: 1 }, "Size limit discarded the last complete frame.");
        }
        Console.WriteLine("SUCCESS Diagnostic recording: exact-pixel roundtrip, aligned detection/input, slow-disk trace retention, bounded queue, disk failure, timeout and size limit.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
