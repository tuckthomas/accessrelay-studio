using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EliteScriptStudio.Application;

public sealed record RegisteredTrainingInput(string Id, string Kind, string Label);
public sealed record OfflineJobView(string Id, string Kind, string State, DateTimeOffset StartedAtUtc, int? ExitCode,
    string? ErrorCode, JsonElement? Report, bool HardwareValidated = false, bool PromotionApproved = false);
sealed class OfflineJob(string id, string kind, string directory, AssistantGrant grant)
{
    internal string Directory = directory;
    internal AssistantGrant Grant = grant;
    internal CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(grant.Cancellation);
    internal OfflineJobView View = new(id, kind, "queued", DateTimeOffset.UtcNow, null, null, null);
    internal Task Completion = Task.CompletedTask;
}

/// <summary>Owns explicitly registered paths and fixed argv builders; external clients receive IDs only.</summary>
public sealed class OfflineJobManager(AssistantSession access, string trainingTools, Func<string, string>? resolveWorkflow = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (RegisteredTrainingInput View, string Path, string? Digest)> _inputs = [];
    private readonly Dictionary<string, OfflineJob> _jobs = [];
    private string? _python, _workspace;
    private long _foregroundGeneration;
    public long ForegroundGeneration { get { lock (_gate) return _foregroundGeneration; } }
    public void Register(string kind, string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType == DriveType.Network))
            throw new AssistantException("localPathRequired", "Choose a local path, not a network share or mapped network drive.");
        if (kind is "dataset" or "workspace")
        {
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new AssistantException("invalidDirectory", "Choose a normal local directory.");
            if (kind == "dataset" && (!File.Exists(System.IO.Path.Combine(path, "session.json")) || !File.Exists(System.IO.Path.Combine(path, "labels.json"))))
                throw new AssistantException("invalidDataset", "Choose a reviewed session directory containing session.json and labels.json.");
        }
        else if (kind is "thresholds" or "python")
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new AssistantException("invalidFile", "Choose a trusted existing local file.");
            if (kind == "thresholds" && new FileInfo(path).Length > 256 * 1024) throw new AssistantException("thresholdBounds", "Threshold JSON exceeds 256 KiB.");
        }
        else throw new AssistantException("invalidInputKind", "Choose dataset, workspace, thresholds or python.");
        lock (_gate)
        {
            if (_jobs.Values.Any(job => job.View.State is "queued" or "running")) throw new AssistantException("jobActive", "Finish or cancel the offline job before changing registered inputs.");
            if (kind == "python") { _python = path; return; }
            if (kind == "workspace") { _workspace = path; return; }
            if (_inputs.Count >= 16) throw new AssistantException("inputLimit", "Restart Studio to reset the registered-input list; at most 16 inputs are allowed.");
            if (_inputs.Values.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            string id = Guid.NewGuid().ToString("N");
            // Labels reveal only kind and ordinal; source folder names can contain personal information.
            var view = new RegisteredTrainingInput(id, kind, kind + " " + (_inputs.Count + 1));
            _inputs.Add(id, (view, path, InputDigest(kind, path)));
        }
    }
    public object RegistrationState(bool includePaths = false)
    {
        lock (_gate)
        {
            if (includePaths) return new { inputs = _inputs.Values.Select(item => new { item.View.Id, item.View.Kind, item.View.Label, path = item.Path }).ToArray(),
                interpreterRegistered = _python is not null, workspaceRegistered = _workspace is not null, interpreterPath = _python, workspacePath = _workspace };
            return new { inputs = _inputs.Values.Select(item => item.View).ToArray(), interpreterRegistered = _python is not null, workspaceRegistered = _workspace is not null };
        }
    }
    public Task<object?> DispatchAsync(AssistantRequest request, CancellationToken cancellation, long? expectedForegroundGeneration = null)
    {
        var grant = access.Require(offline: true); cancellation.ThrowIfCancellationRequested();
        if (request.Operation == "trainingInputs") return Task.FromResult<object?>(RegistrationState());
        if (request.Operation is "trainingJob" or "cancelTrainingJob")
        {
            string id = request.Arguments.GetProperty("jobId").GetString() ?? "";
            lock (_gate)
            {
                var job = _jobs.GetValueOrDefault(id) ?? throw new AssistantException("jobUnavailable", "This job is not available in this Studio session.");
                if (job.Grant.Generation != grant.Generation) throw new AssistantException("jobExpired", "This job belongs to an earlier access window.");
                if (request.Operation == "cancelTrainingJob") job.Stop.Cancel();
                return Task.FromResult<object?>(job.View);
            }
        }
        if (request.Operation is not ("auditDataset" or "visionExperiment")) throw new AssistantException("unknownOperation", "Unsupported offline operation.");
        List<string> arguments = []; string kind = request.Operation == "auditDataset" ? "audit" : "experiment";
        lock (_gate)
        {
            access.Check(grant);
            if (expectedForegroundGeneration is { } expected && expected != _foregroundGeneration)
                throw new AssistantException("foregroundChanged", "Foreground processing changed while the offline request was being checked. Stop processing and request the job again.");
            if (_python is null || _workspace is null) throw new AssistantException("offlineNotConfigured", "Register a trusted Python interpreter and output workspace in Studio Settings.");
            if (_jobs.Values.Any(job => job.View.State is "queued" or "running")) throw new AssistantException("jobActive", "Only one offline job runs at a time.");
            if (_jobs.Count >= 16) throw new AssistantException("jobLimit", "At most 16 jobs run per Studio session. Review local evidence and restart before more jobs.");
            string[] Resolve(string field, string expectedKind)
            {
                var ids = request.Arguments.GetProperty(field).Deserialize<string[]>() ?? [];
                if (ids.Length is < 1 or > 8 || ids.Distinct().Count() != ids.Length) throw new AssistantException("inputBounds", "Choose 1–8 unique registered inputs.");
                return ids.Select(id =>
                {
                    if (!_inputs.TryGetValue(id, out var item) || item.View.Kind != expectedKind) throw new AssistantException("unregisteredInput", "Use an input ID explicitly registered in Studio.");
                    if (InputDigest(item.View.Kind, item.Path) != item.Digest) throw new AssistantException("inputChanged", "Registered source metadata or labels changed. Restart the registration session before using changed inputs.");
                    return item.Path;
                }).ToArray();
            }
            if (kind == "audit")
            { arguments.AddRange(["-S", resolveWorkflow?.Invoke("audit") ?? System.IO.Path.Combine(trainingTools, "Audit-VisionDataset.py"), "--sessions"]); arguments.AddRange(Resolve("datasetIds", "dataset")); }
            else
            {
                string[] train = Resolve("trainingIds", "dataset"), validation = Resolve("validationIds", "dataset");
                if (train.Intersect(validation, StringComparer.OrdinalIgnoreCase).Any()) throw new AssistantException("splitOverlap", "Training and validation must use different sessions.");
                string thresholdId = request.Arguments.GetProperty("thresholdsId").GetString() ?? "";
                if (!_inputs.TryGetValue(thresholdId, out var thresholds) || thresholds.View.Kind != "thresholds" || InputDigest("thresholds", thresholds.Path) != thresholds.Digest)
                    throw new AssistantException("thresholdUnavailable", "Choose unchanged registered thresholds.");
                int epochs = request.Arguments.GetProperty("epochs").GetInt32(), seed = request.Arguments.GetProperty("seed").GetInt32();
                if (epochs is < 1 or > 10 || seed < 0) throw new AssistantException("experimentBounds", "Choose 1–10 epochs and a nonnegative seed.");
                bool planOnly = request.Arguments.GetProperty("planOnly").GetBoolean();
                arguments.AddRange([resolveWorkflow?.Invoke("experiments") ?? System.IO.Path.Combine(trainingTools, "Run-VisionExperiments.py"), "--train"]); arguments.AddRange(train);
                arguments.Add("--validation"); arguments.AddRange(validation);
                arguments.AddRange(["--locked-thresholds", thresholds.Path, "--seeds", seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--epochs", epochs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--cpu-threads", "1", "--device", "cpu",
                    "--target-provider", "CPUExecutionProvider", "--max-run-seconds", "300"]);
                if (planOnly) arguments.Add("--plan-only");
            }
            string id = Guid.NewGuid().ToString("N"), directory = System.IO.Path.Combine(_workspace, "accessrelay-" + id);
            Directory.CreateDirectory(directory);
            arguments.AddRange(["--out", System.IO.Path.Combine(directory, "result")]);
            var job = new OfflineJob(id, kind, directory, grant); _jobs.Add(id, job);
            job.Completion = Task.Run(() => RunAsync(job, _python, arguments));
            return Task.FromResult<object?>(job.View);
        }
    }
    private async Task RunAsync(OfflineJob job, string python, List<string> arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        Process? process = null; WorkerJob? budget = null; Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        OfflineJobView terminal = job.View;
        try
        {
            var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = job.Directory };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["PYTHONUNBUFFERED"] = "1"; start.Environment["OMP_NUM_THREADS"] = "1"; start.Environment["MKL_NUM_THREADS"] = "1";
            deadline.Token.ThrowIfCancellationRequested();
            job.View = job.View with { State = "running" }; process = Process.Start(start) ?? throw new IOException();
            if (OperatingSystem.IsWindows()) budget = WorkerJob.Limit(process, singleProcess: false);
            async Task Log(StreamReader reader, string name)
            {
                await using var log = File.CreateText(System.IO.Path.Combine(job.Directory, name));
                var buffer = new char[2048]; int retained = 0, read;
                while ((read = await reader.ReadAsync(buffer, deadline.Token)) > 0)
                { int take = Math.Min(read, Math.Max(0, 512 * 1024 - retained)); if (take > 0) { await log.WriteAsync(buffer.AsMemory(0, take), deadline.Token); retained += take; } }
            }
            stdout = Log(process.StandardOutput, "stdout.log"); stderr = Log(process.StandardError, "stderr.log");
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested(); process.Refresh();
                if (process.WorkingSet64 > 1536L * 1024 * 1024 || process.TotalProcessorTime > TimeSpan.FromSeconds(300)
                    || StorageExceeded(job.Directory))
                    throw new AssistantException("jobResourceLimit", "Offline resource budget exceeded.");
                await Task.Delay(100, deadline.Token);
            }
            await process.WaitForExitAsync(deadline.Token); budget?.Dispose(); budget = null; await Task.WhenAll(stdout, stderr);
            JsonElement? report = process.ExitCode == 0 ? ReadReport(job) : null;
            terminal = job.View with { State = process.ExitCode == 0 ? "completed" : "failed", ExitCode = process.ExitCode,
                ErrorCode = process.ExitCode == 0 ? null : "workerRejectedInputs", Report = report };
        }
        catch (OperationCanceledException) { terminal = job.View with { State = "cancelled", ErrorCode = "cancelledOrExpired" }; }
        catch (AssistantException error) { terminal = job.View with { State = "failed", ErrorCode = error.Code }; }
        catch (Exception) { terminal = job.View with { State = "failed", ErrorCode = "offlineWorkerFailed" }; }
        finally
        {
            deadline.Cancel();
            if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            budget?.Dispose();
            try { await Task.WhenAll(stdout, stderr); } catch (Exception error) when (error is IOException or OperationCanceledException) { }
            process?.Dispose();
            lock (_gate) job.View = terminal;
        }
    }
    private static JsonElement ReadReport(OfflineJob job)
    {
        string file = job.View.Kind == "audit" ? "audit.json" : File.Exists(System.IO.Path.Combine(job.Directory, "result", "experiment-results.json")) ? "experiment-results.json" : "experiment-plan.json";
        string path = System.IO.Path.Combine(job.Directory, "result", file);
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) throw new AssistantException("reportBounds", "The offline report is missing or exceeds its bounds.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var root = document.RootElement;
        if (job.View.Kind == "audit") return root.Clone();
        // Plan argv and worker error strings contain local paths. Export only fixed summary/metric keys.
        var metrics = new[] { "name", "head", "uncertainty", "seed", "succeeded", "timingMAEms", "timingP99ms", "coverageWithinPredictedUncertainty", "targetShotCoverage", "falseReleaseRate", "cpuInferenceP50ms", "cpuInferenceP95ms", "parameterCount", "modelBytes" };
        return JsonSerializer.SerializeToElement(new { schemaVersion = 1, sourceCorpusSha256 = root.GetProperty("sourceCorpusSha256").GetString(),
            plannedJobs = root.TryGetProperty("jobs", out var jobs) ? jobs.GetArrayLength() : (int?)null,
            results = root.TryGetProperty("results", out var results) ? results.EnumerateArray().Select(item => item.EnumerateObject().Where(property => metrics.Contains(property.Name)).ToDictionary(property => property.Name, property => property.Value.Clone())).ToArray() : [],
            heldOutEvaluated = false, hardwareValidated = false, promotionApproved = false }, AssistantJson.Options);
    }
    public OfflineJobView[] Jobs() { lock (_gate) return _jobs.Values.Select(job => job.View).ToArray(); }
    public void CancelAll()
    {
        lock (_gate)
        {
            _foregroundGeneration++;
            foreach (var job in _jobs.Values) job.Stop.Cancel();
        }
    }
    public async ValueTask DisposeAsync()
    {
        CancelAll(); Task[] tasks; lock (_gate) tasks = _jobs.Values.Select(job => job.Completion).ToArray();
        await Task.WhenAll(tasks); lock (_gate) foreach (var job in _jobs.Values) job.Stop.Dispose();
    }
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static bool StorageExceeded(string directory)
    {
        long bytes = 0; int files = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (++files > 4096) return true;
            try { bytes += new FileInfo(path).Length; } catch (FileNotFoundException) { }
            if (bytes > 256L * 1024 * 1024) return true;
        }
        return false;
    }
    private static string InputDigest(string kind, string path)
    {
        if (kind == "thresholds")
        {
            if (new FileInfo(path).Length > 256 * 1024) throw new AssistantException("thresholdBounds", "Threshold JSON exceeds 256 KiB.");
            return FileHash(path);
        }
        string metadata = System.IO.Path.Combine(path, "session.json"), labels = System.IO.Path.Combine(path, "labels.json");
        if (new FileInfo(metadata).Length > 16 * 1024 * 1024 || new FileInfo(labels).Length > 16 * 1024 * 1024)
            throw new AssistantException("metadataBounds", "Source metadata and labels must each be at most 16 MiB.");
        return FileHash(metadata) + FileHash(labels);
    }
}
