using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;
using EliteScriptStudio.Vision;

internal static class PreviewSessionChecks
{
    // An ordinary owned window for manual UI verification; no capture, model or controller starts.
    internal static void RunSource()
    {
        string className = "AccessRelayPreviewSource_" + Guid.NewGuid().ToString("N");
        bool closed = false;
        Native.WindowProc callback = (hwnd, message, wparam, lparam) =>
        {
            if (message == 0x2) { closed = true; return 0; }
            return PaintWindow(hwnd, message, wparam, lparam);
        };
        nint instance = Native.GetModuleHandle(null);
        var windowClass = new Native.WindowClass { Callback = callback, Instance = instance, ClassName = className };
        nint window = 0;
        bool registered = false;
        try
        {
            registered = Native.RegisterClass(ref windowClass) != 0;
            Check(registered, "Synthetic preview source registration failed.");
            window = Native.CreateWindowEx(0, className, "AccessRelay synthetic preview source", 0x10CF0000,
                80, 80, 640, 480, 0, 0, instance, 0);
            Check(window != 0, "Synthetic preview source creation failed.");
            Native.UpdateWindow(window);
            Check(Native.SetTimer(window, 1, 16, 0) != 0, "Synthetic preview source animation failed.");
            var timeout = Stopwatch.StartNew();
            while (!closed && timeout.Elapsed < TimeSpan.FromMinutes(4)) { Pump(); Thread.Sleep(5); }
        }
        finally
        {
            if (window != 0 && !closed) Native.DestroyWindow(window);
            if (registered) Native.UnregisterClass(className, instance);
            GC.KeepAlive(callback);
        }
    }

    internal static void Run(string? diagnosticsPath = null)
    {
        Marshal.ThrowExceptionForHR(Native.RoInitialize(1));
        string className = "AccessRelayPreviewCheck_" + Guid.NewGuid().ToString("N");
        Native.WindowProc callback = PaintWindow;
        nint instance = Native.GetModuleHandle(null);
        var windowClass = new Native.WindowClass { Callback = callback, Instance = instance, ClassName = className };
        nint window = 0;
        bool registered = false;
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelayPreviewVerification-" + Guid.NewGuid().ToString("N"));
        try
        {
            registered = Native.RegisterClass(ref windowClass) != 0;
            Check(registered, "Preview fixture window registration failed.");
            window = Native.CreateWindowEx(0, className, "AccessRelay preview lifecycle verification", 0x10CF0000,
                80, 80, 320, 240, 0, 0, instance, 0);
            Check(window != 0, "Preview fixture window creation failed.");
            Native.UpdateWindow(window);
            Check(Native.SetTimer(window, 1, 16, 0) != 0, "Preview fixture animation timer failed.");

            using var input = new EliteInputMonitor();
            var physical = new ControllerState((ushort)(ControlId.X | ControlId.A), 40, 60, 10000, -11000, 12000, -13000);
            input.SetState(physical);
            using var runtime = new StudioRuntime(input, StudioProfile.CreateDefault() with { ScriptsEnabled = false }, new(0, 0));
            runtime.Attach(new UsbipXboxOutput());
            using var session = new VisionSession(runtime);
            var profile = new Nba2K27ShotProfile(Guid.NewGuid(), "Preview regression fixture", 78, 80,
                "Fixture base", "Fixture upper one", "Fixture upper two");
            var settings = VisionSettings.Default with
            {
                AnimationProfile = "nba2k27:" + profile.ConfigurationFingerprint,
                CameraView = profile.CameraView
            };
            // One capture-only session exercises the real worker and recorder without an ONNX native runtime.
            session.Start(window, settings);
            WaitFor(() => session.Status.Frames >= 4, "Hidden capture did not advance", session);
            WaitFor(() => session.Performance.TotalFrames >= 1, "Hidden capture did not produce any valid timing sample", session);
            AssertNoPreview(session, "A new session published a preview without explicit visibility.");
            Check(session.Performance.ExecutionProvider == "Capture only" && !session.Status.ModelReady,
                "The fixture unexpectedly loaded an inference backend.");
            AssertPassthrough(runtime, physical);
            long generation = runtime.VisionStatus.SessionGeneration;

            session.SetPreviewEnabled(true);
            using var held = WaitPreview(session);
            Check(held.Width > 100 && held.Height > 100 && PixelChecks.AnyNonzero(held.Pixels.AsSpan(0, held.Length)),
                "Visible preview did not contain captured pixels.");
            byte[] heldPixels = held.Pixels.AsSpan(0, held.Length).ToArray();

            // Leave the next low-rate preview unconsumed, then disable it while capture is active.
            PumpFor(TimeSpan.FromMilliseconds(250));
            long beforePending = session.Status.Frames;
            WaitFor(() => session.Status.Frames >= beforePending + 3, "Capture stalled before disabling a pending preview", session);
            session.SetPreviewEnabled(false);
            AssertNoPreview(session, "Disabling preview retained a pending preview buffer.");
            long hiddenFrames = session.Status.Frames;
            WaitFor(() => session.Status.Frames >= hiddenFrames + 5, "Disabling preview stopped the capture worker", session);
            AssertNoPreview(session, "Hidden capture republished a preview after disable.");
            Check(held.Pixels.AsSpan(0, held.Length).SequenceEqual(heldPixels),
                "Disabling preview invalidated a buffer already owned by the consumer.");

            long priorSequence = held.Sequence;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                long frames = session.Status.Frames;
                session.SetPreviewEnabled(true);
                using var next = WaitPreview(session);
                Check(next.Sequence > priorSequence, "Re-enabling preview reused an old captured frame.");
                priorSequence = next.Sequence;
                session.SetPreviewEnabled(false);
                AssertNoPreview(session, "Repeated preview disable retained a pending buffer.");
                Check(session.Status.Frames >= frames && runtime.VisionStatus.SessionGeneration == generation,
                    "Preview visibility restarted or invalidated the observation session.");
            }
            Check(held.Pixels.AsSpan(0, held.Length).SequenceEqual(heldPixels),
                "Later previews overwrote a consumer-owned preview buffer.");
            AssertPassthrough(runtime, physical);

            Directory.CreateDirectory(directory);
            session.StartTraining(directory, profile, "Synthetic preview regression window");
            long recordingFrames = session.Status.Frames;
            WaitFor(() => session.Status.Frames >= recordingFrames + 5, "Hidden training capture stopped advancing", session);
            Check(session.IsRecordingTraining && runtime.IsVisionTrainingRecording,
                "Hidden preview stopped the training take or its authoritative guard.");
            AssertNoPreview(session, "Training capture published a hidden preview.");
            AssertPassthrough(runtime, physical);
            session.StopTraining();
            WaitFor(() => !runtime.IsVisionTrainingRecording, "Hidden training take did not finalize", session);
            string[] metadataPaths = Directory.GetFiles(directory, "session.json", SearchOption.AllDirectories);
            Check(metadataPaths.Length == 1, "Preview toggles created more than one training take.");
            using (var metadata = JsonDocument.Parse(File.ReadAllText(metadataPaths[0])))
            {
                Check(metadata.RootElement.GetProperty("complete").GetBoolean() &&
                    metadata.RootElement.GetProperty("frames").GetArrayLength() >= 3,
                    "The hidden training take did not persist captured frames.");
            }

            session.SetPreviewEnabled(true);
            using (var resumed = WaitPreview(session))
                Check(resumed.Sequence > priorSequence, "Preview did not resume after hidden recording.");
            session.Stop();
            AssertNoPreview(session, "Stop retained a pending preview.");
            WaitFor(() => !session.Status.Running, "Preview session did not stop", session);
            PumpFor(TimeSpan.FromMilliseconds(250));
            AssertNoPreview(session, "An in-flight worker republished a preview after stop.");
            AssertPassthrough(runtime, physical);
            if (diagnosticsPath is not null) session.ExportDiagnostics(diagnosticsPath);
            Console.WriteLine("SUCCESS: real session capture continues with preview hidden; visibility toggles preserve the session, consumer leases, training frames and manual output; stop clears pending previews.");
        }
        finally
        {
            if (window != 0) Native.DestroyWindow(window);
            if (registered) Native.UnregisterClass(className, instance);
            Native.RoUninitialize();
            GC.KeepAlive(callback);
            if (Directory.Exists(directory))
            {
                string resolved = Path.GetFullPath(directory);
                string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(Path.GetDirectoryName(resolved), temporary, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(resolved).StartsWith("AccessRelayPreviewVerification-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the owned preview verification directory.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private static void AssertPassthrough(StudioRuntime runtime, ControllerState physical) =>
        WaitFor(() => runtime.Latest is { OutputActive: true } sample && sample.Output == physical,
            "Preview visibility disturbed physical controller passthrough");

    private static void AssertNoPreview(VisionSession session, string message)
    {
        using var unexpected = session.TakePreview();
        Check(unexpected is null, message);
    }

    private static VisionPreview WaitPreview(VisionSession session)
    {
        VisionPreview? preview = null;
        WaitFor(() => (preview = session.TakePreview()) is not null, "Visible preview did not arrive", session);
        return preview!;
    }

    private static void WaitFor(Func<bool> condition, string message, VisionSession? session = null)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException(message + ". " + session?.Status.Message +
                    $" SessionFrames={session?.Status.Frames}; PerformanceFrames={session?.Performance.TotalFrames}; localCaptureAgeMs={(session is null ? 0 : Stopwatch.GetElapsedTime(session.Status.LatestCaptureTicks).TotalMilliseconds):F2}.");
            Thread.Sleep(5);
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < duration) { Pump(); Thread.Sleep(5); }
    }

    private static void Pump()
    {
        while (Native.PeekMessage(out var message, 0, 0, 0, 1))
        { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
    }

    private static nint PaintWindow(nint hwnd, uint message, nint wparam, nint lparam)
    {
        if (message == 0x113) { Native.InvalidateRect(hwnd, 0, false); return 0; }
        if (message == 0xF)
        {
            nint dc = Native.BeginPaint(hwnd, out var paint);
            nint brush = Native.CreateSolidBrush((uint)(Environment.TickCount & 0x00FFFFFF));
            Native.FillRect(dc, ref paint.Rectangle, brush);
            Native.DeleteObject(brush); Native.EndPaint(hwnd, ref paint);
            return 0;
        }
        return Native.DefWindowProc(hwnd, message, wparam, lparam);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
