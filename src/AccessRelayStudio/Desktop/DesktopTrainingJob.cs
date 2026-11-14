using System.Diagnostics;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Desktop;

/// <summary>Host-owned local worker lifecycle. Reviewed game adapters choose the workflow.</summary>
internal sealed class DesktopTrainingJob : IDisposable
{
    internal sealed record Setup(string DatasetRoot = "", string WorkspaceRoot = "", string Python = "");
    private readonly object _gate = new();
    private readonly string _settingsFile = Path.Combine(AppContext.BaseDirectory, "TrainingSettings.json");
    private CancellationTokenSource? _stop;
    private Task? _task;
    private string _state = "idle", _message = "", _directory = "";
    private JsonElement? _result;
    internal Setup Configuration { get; private set; } = new();
    internal bool Running { get { lock (_gate) return _state == "running"; } }
    internal string CandidateManifest { get { lock (_gate) return Path.Combine(_directory, "candidate", "manifest.json"); } }
    internal object Snapshot { get { lock (_gate) return new { state = _state, message = _message, directory = _directory, result = _result }; } }
    internal DesktopTrainingJob()
    {
        try { if (File.Exists(_settingsFile) && new FileInfo(_settingsFile).Length <= 64 * 1024) Configuration = JsonSerializer.Deserialize<Setup>(File.ReadAllBytes(_settingsFile), DesktopJson.Options) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        Configuration = new(Configuration.DatasetRoot ?? "", Configuration.WorkspaceRoot ?? "", Configuration.Python ?? "");
        string python = Path.Combine(AppContext.BaseDirectory, "TrainingPython", "Scripts", "python.exe");
        Configuration = Configuration with { DatasetRoot = Configuration.DatasetRoot.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "TrainingData") : Configuration.DatasetRoot,
            WorkspaceRoot = Configuration.WorkspaceRoot.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "TrainingResults") : Configuration.WorkspaceRoot,
            Python = Configuration.Python.Length == 0 && File.Exists(python) ? python : Configuration.Python };
        try { Directory.CreateDirectory(Configuration.DatasetRoot); Directory.CreateDirectory(Configuration.WorkspaceRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Configuration = Configuration with { DatasetRoot = "", WorkspaceRoot = "" }; }
    }
    internal void Configure(string kind, string path)
    {
        if (Running) throw new InvalidOperationException("Stop training before changing its folders or interpreter.");
        string full = Path.GetFullPath(path);
        Configuration = kind switch { "dataset" => Configuration with { DatasetRoot = full }, "workspace" => Configuration with { WorkspaceRoot = full },
            "python" => Configuration with { Python = full }, _ => throw new InvalidDataException("Unknown training setting.") };
        File.WriteAllBytes(_settingsFile, JsonSerializer.SerializeToUtf8Bytes(Configuration, DesktopJson.Options));
    }
    internal void Start(string script, JsonElement options)
    {
        lock (_gate)
        {
            if (_state == "running") throw new InvalidOperationException("A training job is already running.");
            if (!Directory.Exists(Configuration.DatasetRoot) || !Directory.Exists(Configuration.WorkspaceRoot) || !File.Exists(Configuration.Python))
                throw new InvalidOperationException("Choose the training data folder, model output folder and Python interpreter in Model Training settings.");
            string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Configuration.DatasetRoot));
            string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Configuration.WorkspaceRoot));
            if (workspace.Equals(data, StringComparison.OrdinalIgnoreCase) || workspace.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Keep model outputs outside the original training data folder.");
            script = Path.GetFullPath(script);
            if (!File.Exists(script) || Path.GetExtension(script) != ".py")
                throw new InvalidOperationException("The selected plugin's training workflow is unavailable.");
            _directory = Path.Combine(workspace, "training-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_directory);
            string request = Path.Combine(_directory, "request.json");
            File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(new { Configuration.DatasetRoot, outputRoot = _directory, options }, DesktopJson.Options));
            _stop?.Dispose(); _stop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            _state = "running"; _message = "Preparing reviewed clips, training and evaluating the candidate."; _result = null;
            _task = RunAsync(Configuration.Python, script, request, _directory, _stop.Token);
        }
    }
    private async Task RunAsync(string python, string script, string request, string directory, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory } };
        process.StartInfo.ArgumentList.Add(script); process.StartInfo.ArgumentList.Add("--request"); process.StartInfo.ArgumentList.Add(request);
        process.StartInfo.Environment["PYTHONUNBUFFERED"] = "1";
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "1";
        process.StartInfo.Environment["MKL_NUM_THREADS"] = "1";
        process.StartInfo.Environment["TORCH_HOME"] = Path.Combine(directory, "cache", "torch");
        process.StartInfo.Environment["CUDA_CACHE_PATH"] = Path.Combine(directory, "cache", "cuda");
        process.StartInfo.Environment["TEMP"] = directory; process.StartInfo.Environment["TMP"] = directory;
        string lastError = "";
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        try
        {
            process.Start();
            using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            stdout = Drain(process.StandardOutput, Path.Combine(directory, "stdout.log"), null);
            stderr = Drain(process.StandardError, Path.Combine(directory, "stderr.log"), line => lastError = line);
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Training worker exited with code {process.ExitCode}. {lastError}");
            string resultFile = Path.Combine(directory, "workflow-result.json");
            if (!File.Exists(resultFile) || new FileInfo(resultFile).Length > 1024 * 1024) throw new InvalidDataException("The training result is missing or exceeds its bound.");
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(resultFile, cancellation).ConfigureAwait(false));
            lock (_gate) { _result = json.RootElement.Clone(); _state = "succeeded";
                _message = json.RootElement.TryGetProperty("validationSucceeded", out var validated) && validated.ValueKind == JsonValueKind.True
                    ? "Candidate trained and evaluated. It remains observation-only."
                    : "Candidate trained, but validation failed. Review the job logs before importing."; }
        }
        catch (OperationCanceledException) { lock (_gate) { _state = "cancelled"; _message = "Training cancelled or reached its 30-minute limit. Partial outputs remain in the job folder."; } }
        catch (Exception error) { lock (_gate) { _state = "failed"; _message = error.Message; } }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (IOException) { }
        }
    }
    private static async Task Drain(StreamReader reader, string path, Action<string>? observe)
    {
        await using var output = new StreamWriter(path);
        long written = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            observe?.Invoke(line.Length > 2000 ? line[..2000] : line);
            if (written < 2 * 1024 * 1024) { await output.WriteLineAsync(line.AsMemory(0, Math.Min(line.Length, 4000))).ConfigureAwait(false); written += Math.Min(line.Length, 4000) + 1; }
        }
    }
    internal void Cancel() { lock (_gate) _stop?.Cancel(); }
    public void Dispose() => Cancel();
}
