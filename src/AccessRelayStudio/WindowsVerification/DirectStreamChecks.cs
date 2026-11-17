using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;
using EliteScriptStudio.Vision;

internal static class DirectStreamChecks
{
    internal static void Run()
    {
        CheckCapture();
        CheckVisionSession();
        CheckWriter();
        CheckRuntimeOutput();
        Console.WriteLine("PASS direct Xbox capture leases, vision preview, upstream gamepad reports and runtime output routing");
    }

    private static void CheckCapture()
    {
        var track = new TestTrack();
        using var capture = new DecodedStreamCapture(track);
        track.Frame(2, 2, 1);
        Check(capture.TryAcquireLatest(out var first), "No decoded frame.");
        using var held = first!;
        Check(held.Stride == 8 && held.Pixels.Length == 16 && held.Pixels.Span[8] == 2
            && held.CompositorTicks is null && held.CaptureTicks > 0
            && capture.Status.Pipeline?.CaptureTimeBasis == CapturePipelineSnapshot.DecoderReceipt, "Row pitch or clock provenance was lost.");
        track.Frame(2, 2, 2);
        track.Frame(3, 2, 3);
        track.Frame(3, 2, 4);
        Check(capture.TryAcquireLatest(out var next), "No replacement frame.");
        using var replacement = next!;
        Check(replacement.Width == 3 && replacement.SourceGeneration > held.SourceGeneration
            && replacement.Sequence == 4 && capture.Status.Pipeline?.PendingReplacements == 1,
            "Resize or latest-frame replacement failed.");
        long changed = capture.Status.ContentChangedTicks;
        track.Frame(3, 2, 4);
        Check(capture.Status.ContentChangedTicks == changed, "Duplicate content reset the freshness clock.");
        Check(capture.TryAcquireLatest(out var last), "Third frame slot unavailable.");
        using var third = last!;
        track.Frame(3, 2, 5);
        Check(capture.Status.Pipeline?.BufferUnavailable == 1, "Outstanding leases did not bound storage.");
        track.End();
        track.Frame(2, 2, 6);
        Check(!capture.Status.IsRunning && !capture.TryAcquireLatest(out _)
            && held.Pixels.Span[0] == 1, "Track end revived a source or invalidated an outstanding lease.");
        capture.Dispose();
        track.Frame(2, 2, 7); // Simulate an already dispatched callback after unsubscribe.
        Check(track.Unsubscribed && held.Pixels.Span[0] == 1, "Disposal corrupted a consumer-owned frame.");

        var invalid = new TestTrack();
        using var invalidCapture = new DecodedStreamCapture(invalid);
        invalid.Frame(2, 2, 8, availableBytes: 4);
        Check(!invalidCapture.Status.IsRunning && !invalidCapture.TryAcquireLatest(out _), "Truncated frame accepted.");
    }

    private static void CheckVisionSession()
    {
        using var input = new FakeControllerInput();
        using var runtime = new StudioRuntime(input, StudioProfile.CreateDefault(), new(0, 0));
        using var vision = new VisionSession(runtime);
        var track = new TestTrack();
        vision.SetPreviewEnabled(true);
        vision.StartDirect(track, VisionSettings.Default, loadModel: false);
        Wait(() => track.Subscribed, "Vision did not subscribe to the decoder.");
        track.Frame(8, 4, 55);
        Wait(() => vision.Status.Frames > 0, "The vision worker did not consume the decoded frame.");
        using var preview = vision.TakePreview();
        Check(preview is { Width: 8, Height: 4 } && preview.Pixels[0] == 55 && !runtime.HasOutput,
            "Observation-only direct preview changed the image or enabled controller output.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var crop = vision.CaptureRegionOnceAsync(new(0, .5, 1, .5), timeout.Token);
        track.Frame(8, 4, 60);
        Check(crop.GetAwaiter().GetResult() is { Width: 8, Height: 2 } region && region.Pixels[0] == 62,
            "The existing crop consumer did not receive decoded pixels.");
        string recordings = Path.Combine(Path.GetTempPath(), "AccessRelay-direct-" + Guid.NewGuid().ToString("N"));
        try
        {
            vision.StartDiagnosticRecording(recordings, new { source = "Decoded test track" });
            track.Frame(8, 4, 70);
            Wait(() => vision.DiagnosticSnapshot?.ObservedFrames >= 1, "Direct diagnostic recorder missed a frame.");
            vision.StopDiagnosticRecording();
            vision.FinishDiagnosticRecordingAsync(timeout.Token).GetAwaiter().GetResult();
            var manifest = Directory.GetFiles(recordings, "diagnostic.json", SearchOption.AllDirectories).Single();
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            Check(json.RootElement.GetProperty("context").GetProperty("captureTimeBasis").GetString() == CapturePipelineSnapshot.DecoderReceipt &&
                json.RootElement.GetProperty("storedFrames").GetInt32() == 1, "Direct recording lost its pixels or clock provenance.");
        }
        finally { if (Directory.Exists(recordings)) Directory.Delete(recordings, true); }
        track.End();
        Wait(() => !vision.Status.Running && track.Unsubscribed, "Track loss did not stop observation.");
        Check(vision.Status.CaptureFailed && !vision.Status.CanArm, "Track loss was reported as ready.");
    }

    private static void CheckWriter()
    {
        var channel = new TestChannel();
        using var writer = new XboxStreamControllerOutput(channel);
        Check(writer.TryWrite(default), "Neutral send failed.");
        Check(writer.TryWrite(new((ushort)(ControlId.A | ControlId.View | ControlId.RightBumper
            | ControlId.DPadLeft | ControlId.RightStick), 128, 255, short.MinValue, short.MinValue, 32767, 16383)),
            "Mixed control send failed.");
        Check(writer.TryWrite(new(0xF7FF, 0, 0, 0, 0, 0, 0)), "Combined button send failed.");

        // Generated by the pinned player's InputPacket.toBuffer() with performance.now() = 12.5.
        // Only the clock bytes vary between the two implementations.
        string[] expected =
        [
            "080000000000000000000000294001",
            "0200010000000000000000002940010000000000000000000000000000000100000000000001",
            "0200020000000000000000002940010018a401800180ff7fff3f8080ffff0100000000000001",
            "02000300000000000000000029400100feff0000000000000000000000000100000000000001"
        ];
        var reports = channel.Snapshot();
        Check(reports.Length == expected.Length, "Unexpected input report count.");
        double previous = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            var actual = reports[i].ToArray();
            double time = BinaryPrimitives.ReadDoubleLittleEndian(actual.AsSpan(6));
            Check(double.IsFinite(time) && time >= previous, "Input channel clock moved backwards.");
            previous = time;
            BinaryPrimitives.WriteDoubleLittleEndian(actual.AsSpan(6), 12.5);
            Check(actual.SequenceEqual(Convert.FromHexString(expected[i])), $"Upstream report {i} differs.");
        }
        channel.Reject = true;
        Check(!writer.TryWrite(default) && !writer.IsAvailable, "Backpressure was counted as a successful write.");
        channel.Reject = false;
        writer.Dispose();
        Check(!channel.IsOpen && IsNeutral(channel.Snapshot()[^1]), "Output teardown did not neutralize and close the channel.");

        var unavailable = new TestChannel { Reject = true };
        using var failed = new XboxStreamControllerOutput(unavailable);
        Check(!failed.IsAvailable && !failed.TryWrite(default), "A rejected handshake enabled controller output.");
    }

    private static void CheckRuntimeOutput()
    {
        using var input = new FakeControllerInput();
        using var runtime = new StudioRuntime(input, StudioProfile.CreateDefault() with { ScriptsEnabled = false }, new(0, 0));
        var channel = new TestChannel();
        runtime.Attach(new XboxStreamControllerOutput(channel));
        input.SetState(new((ushort)ControlId.A, 0, 0, 0, 0, 0, short.MaxValue));
        Wait(() => channel.Snapshot().Any(p => p.Length == 38 && p[16] == 16 &&
            BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(24)) == short.MaxValue), "Physical input missed the direct channel.");
        Check(!input.VirtualOutputActive, "Direct output reserved the virtual USB identity.");
        // Keep the physical stick down while the C# scheduler emits the reversal.
        input.SetState(default);
        long resetAt = Stopwatch.GetTimestamp();
        Wait(() => runtime.ReadVisionSignalContext() is { Physical: { } physical, InputTimestamp: var timestamp }
            && timestamp > resetAt && physical == default, "Controller did not return to neutral.");
        runtime.ConfigureVision(VisionSettings.Default with { MaximumFrameAgeMs = 1000, MaximumLatenessMs = 100 },
            new() { Kind = VisionActionKind.RightStickStroke, StrokeDurationMs = 56, AnchorFraction = .85 });
        runtime.BeginVisionSession();
        Check(runtime.SetVisionArmed(true), "Could not arm the direct output fixture.");
        input.SetState(default(ControllerState) with { RightY = short.MinValue });
        Wait(() => runtime.VisionStatus.ShotToken != 0, "Held input did not qualify a stroke.");
        var shot = runtime.VisionStatus;
        long now = Stopwatch.GetTimestamp();
        Check(runtime.SubmitVisionPrediction(new(shot.SessionGeneration, shot.ShotToken, 1, now, now, 100, 1, 0, true)),
            "Direct output rejected a qualified forecast.");
        Wait(() => runtime.LastVisionAction?.ActualAnchorTimestamp > 0, "Scheduled stroke missed its output receipt.");
        var stroke = channel.Snapshot().Where(report => report.Length == 38)
            .Select(report => BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(24))).ToList();
        int downIndex = stroke.IndexOf(-32767);
        Check(downIndex >= 0 && stroke.IndexOf(short.MaxValue, downIndex) > downIndex,
            "Scheduled full-up endpoint missed the stream while physical input stayed down.");

        runtime.StopOutput(false);
        Check(!channel.IsOpen && IsNeutral(channel.Snapshot()[^1]), "Stopping runtime output did not release the gamepad.");
        runtime.Attach(new FakeControllerOutput());
        Check(input.VirtualOutputActive, "Switching back did not reserve the virtual USB identity.");
    }

    private static bool IsNeutral(byte[] report) => report.Length == 38 && report.AsSpan(16, 14).IndexOfAnyExcept((byte)0) < 0;
    private static void Wait(Func<bool> condition, string message) =>
        Check(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestChannel : IXboxInputChannel
    {
        private readonly List<byte[]> _reports = [];
        public bool IsOpen { get; private set; } = true;
        internal bool Reject { get; set; }
        public bool TrySend(ReadOnlySpan<byte> report)
        {
            if (!IsOpen || Reject) return false;
            lock (_reports) _reports.Add(report.ToArray());
            return true;
        }
        internal byte[][] Snapshot() { lock (_reports) return _reports.ToArray(); }
        public void Dispose() => IsOpen = false;
    }

    private sealed class TestTrack : IDecodedVideoTrack, IDisposable
    {
        private Action<DecodedBgraFrame>? _frame;
        private Action? _end;
        private int _unsubscribed;
        internal bool Subscribed => Volatile.Read(ref _frame) is not null;
        internal bool Unsubscribed => Volatile.Read(ref _unsubscribed) != 0;
        public IDisposable Subscribe(Action<DecodedBgraFrame> onFrame, Action onEnded)
        {
            _end = onEnded;
            Volatile.Write(ref _frame, onFrame);
            return this;
        }
        internal void Frame(int width, int height, byte value, int? availableBytes = null)
        {
            int pitch = width * 4 + 8;
            byte[] bytes = new byte[pitch * height];
            for (int y = 0; y < height; y++) bytes.AsSpan(y * pitch, width * 4).Fill((byte)(value + y));
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { _frame!(new(pin.AddrOfPinnedObject(), availableBytes ?? bytes.Length, pitch, width, height)); }
            finally { pin.Free(); }
        }
        internal void End() => _end!();
        public void Dispose() => Volatile.Write(ref _unsubscribed, 1);
    }
}
