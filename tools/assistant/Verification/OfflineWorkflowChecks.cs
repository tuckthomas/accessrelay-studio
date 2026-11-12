using System.Text.Json;
using EliteScriptStudio.Application;

static class OfflineWorkflowChecks
{
    internal static async Task Run(string[] args)
    {
        int pythonIndex = Array.IndexOf(args, "--python");
        string python = pythonIndex >= 0 ? args[pythonIndex + 1] : OperatingSystem.IsWindows() ? throw new Exception("Pass --python with the trusted test interpreter") : "/usr/bin/python3";
        python = new FileInfo(python).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(python);
        string root = Path.Combine(Path.GetTempPath(), "AccessRelayAssistant-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var access = new AssistantSession(); access.Configure(new(true, Offline: true));
        try
        {
            string train = MakeSession(root, "train", 20), validation = MakeSession(root, "validation", 120);
            string thresholds = Path.Combine(root, "thresholds.json");
            File.WriteAllText(thresholds, JsonSerializer.Serialize(new { schemaVersion = 1, animationProfile = "nba2k27:" + new string('a',64), cameraConfiguration = "2K", executionProvider = "CPUExecutionProvider" }));
            await using var jobs = new OfflineJobManager(access, Path.GetFullPath("games/nba2k27/training"));
            jobs.Register("python", python); jobs.Register("workspace", root); jobs.Register("dataset", train); jobs.Register("dataset", validation); jobs.Register("thresholds", thresholds);
            var registration = JsonSerializer.SerializeToElement(jobs.RegistrationState(), AssistantJson.Options);
            var inputs = registration.GetProperty("inputs").EnumerateArray().ToArray();
            string trainId = inputs[0].GetProperty("id").GetString()!, valId = inputs[1].GetProperty("id").GetString()!, thresholdId = inputs[2].GetProperty("id").GetString()!;
            Check(!registration.GetRawText().Contains(root) && !registration.GetRawText().Contains("session.json"), "Registration leaked paths.");
            // A foreground action between the native idle read and job admission
            // must reject both operations, even if no job existed to cancel yet.
            long foreground = jobs.ForegroundGeneration;
            jobs.CancelAll();
            foreach (string operation in new[] { "auditDataset", "visionExperiment" })
            {
                try { await jobs.DispatchAsync(Request(operation, new { }), default, foreground); throw new Exception("Stale foreground job admitted"); }
                catch (AssistantException error) when (error.Code == "foregroundChanged") { }
            }
            Check(jobs.Jobs().Length == 0 && !Directory.EnumerateDirectories(root, "accessrelay-*").Any(), "Rejected job created work or artifacts.");
            await StartAndWait(jobs, "auditDataset", new { datasetIds = new[] { trainId, valId } });
            var audit = jobs.Jobs().Last(); Check(audit.State == "completed" && audit.Report?.GetProperty("sessionCount").GetInt32() == 2, "Actual stdlib audit failed: " + audit.ErrorCode);
            await StartAndWait(jobs, "visionExperiment", new { trainingIds = new[] { trainId }, validationIds = new[] { valId }, thresholdsId = thresholdId, planOnly = true, epochs = 1, seed = 42 });
            var plan = jobs.Jobs().Last(); Check(plan.State == "completed" && plan.Report?.GetProperty("plannedJobs").GetInt32() == 6 && !plan.Report.Value.GetRawText().Contains(root), "Actual experiment plan failed or leaked argv paths: " + plan.ErrorCode);
            try { await jobs.DispatchAsync(Request("auditDataset", new { datasetIds = new[] { "../../unregistered" } }), default); throw new Exception("Unknown path accepted"); }
            catch (AssistantException error) when (error.Code == "unregisteredInput") { }
            File.AppendAllText(Path.Combine(train, "labels.json"), " ");
            try { await jobs.DispatchAsync(Request("auditDataset", new { datasetIds = new[] { trainId } }), default); throw new Exception("Changed labels accepted"); }
            catch (AssistantException error) when (error.Code == "inputChanged") { }
            access.Configure(new());
            try { await jobs.DispatchAsync(Request("trainingInputs", new { }), default); throw new Exception("Revoked job API succeeded"); }
            catch (AssistantException error) when (error.Code == "accessDisabled") { }
            access.Configure(new(true, Offline: true));
            string fakeTools = Path.Combine(root, "fake-tools"); Directory.CreateDirectory(fakeTools);
            File.WriteAllText(Path.Combine(fakeTools, "Audit-VisionDataset.py"), """
import pathlib, subprocess, sys, time
child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
pathlib.Path(sys.argv[sys.argv.index('--out') + 1]).parent.joinpath('child.pid').write_text(str(child.pid))
time.sleep(60)
""");
            var stalled = new OfflineJobManager(access, fakeTools); bool disposed = false;
            try
            {
                stalled.Register("python", python); stalled.Register("workspace", root); stalled.Register("dataset", train);
                var known = JsonSerializer.SerializeToElement(stalled.RegistrationState(), AssistantJson.Options).GetProperty("inputs")[0].GetProperty("id").GetString()!;
                await stalled.DispatchAsync(Request("auditDataset", new { datasetIds = new[] { known } }), default);
                string childFile = Path.Combine(root, "accessrelay-" + stalled.Jobs()[0].Id, "child.pid");
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(childFile)) await Task.Delay(25, wait.Token);
                int childPid = int.Parse(File.ReadAllText(childFile));
                access.Configure(new()); await stalled.DisposeAsync(); disposed = true;
                Check(stalled.Jobs()[0].State == "cancelled", "Revoked background job did not cancel.");
                using var exitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (true)
                {
                    try
                    {
                        using var child = System.Diagnostics.Process.GetProcessById(childPid);
                        if (child.HasExited) break;
                        // In minimal Linux containers PID 1 may retain a killed orphan as a zombie.
                        if (!OperatingSystem.IsWindows() && File.ReadAllText($"/proc/{childPid}/stat").Split(')')[1].TrimStart().StartsWith('Z')) break;
                    }
                    catch (Exception error) when (error is ArgumentException or FileNotFoundException or DirectoryNotFoundException) { break; }
                    await Task.Delay(25, exitLimit.Token);
                }
            }
            catch { if (!disposed) await stalled.DisposeAsync(); throw; }
            Console.WriteLine("PASS real stdlib audit and six-job plan-only runner, foreground admission races, registered IDs, private-path redaction, source changes and revoked offline access.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    static async Task StartAndWait(OfflineJobManager manager, string operation, object args)
    {
        await manager.DispatchAsync(Request(operation, args), default, manager.ForegroundGeneration);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (manager.Jobs().Last().State is "queued" or "running") await Task.Delay(25, timeout.Token);
        if (manager.Jobs().Last().State != "completed")
            throw new Exception("Offline fixture job failed: " + JsonSerializer.Serialize(manager.Jobs().Last(), AssistantJson.Options));
    }
    static AssistantRequest Request(string operation, object args) => new(operation, JsonSerializer.SerializeToElement(args, AssistantJson.Options));
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static string MakeSession(string root, string split, int color)
    {
        string directory = Path.Combine(root, split); Directory.CreateDirectory(directory); string id = Guid.NewGuid().ToString("N");
        using (var pixels = File.Create(Path.Combine(directory, "frames.bgra")))
            for (int frame = 0; frame < 12; frame++)
            {
                var data = new byte[256*256*4];
                for (int index = 0; index < data.Length; index += 4) { data[index] = (byte)(color + frame); data[index + 1] = 20; data[index + 2] = 30; data[index + 3] = 255; }
                pixels.Write(data);
            }
        File.WriteAllText(Path.Combine(directory,"session.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, sessionId = id, shotProfileFingerprint = new string('a',64), complete = true,
            stopwatchFrequency = 1000000, pixelFormat = "BGRA8", storedWidth = 256, storedHeight = 256, storedStride = 1024,
            shotProfile = new { cameraView = "2K" },
            frames = Enumerable.Range(0,12).Select(index => new { sequence = index+1, captureTicks = 100000+index*17000, file = "frames.bgra", offsetBytes = index*256*256*4, byteLength = 256*256*4 }) }));
        File.WriteAllText(Path.Combine(directory,"labels.json"), JsonSerializer.Serialize(new { schemaVersion = 1, sessionId = id, split,
            labelTimeBasis = "captured-target-game-phase", shots = new[] {
                new { shotId = "target", reviewed = true, startTicks = 100000, endTicks = 202000, targetReleaseTicks = 185000, isTargetShot = true, playerIdentityConfirmed = true },
                new { shotId = "negative", reviewed = true, startTicks = 219000, endTicks = 287000, targetReleaseTicks = 0, isTargetShot = false, playerIdentityConfirmed = false } } }));
        return directory;
    }
}
