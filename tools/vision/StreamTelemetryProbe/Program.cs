using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

if (args is ["--self-test"]) { MetricFields.Verify(); return 0; }
if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Live ETW discovery requires Windows. --self-test runs on any platform."); return 1; }
int seconds = 30;
string output = Path.GetFullPath($"xbox-stream-telemetry-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
var pids = new HashSet<int>();
bool verbose = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--verbose") { verbose = true; continue; }
    if (i + 1 >= args.Length) return Usage();
    string option = args[i], value = args[++i];
    switch (option)
    {
        case "--pid" when int.TryParse(value, out int pid) && pid > 0: pids.Add(pid); break;
        case "--seconds" when int.TryParse(value, out int duration) && duration is >= 5 and <= 120: seconds = duration; break;
        case "--output": output = Path.GetFullPath(value); break;
        default: return Usage();
    }
}
if (pids.Count == 0)
    foreach (var process in Process.GetProcesses())
        using (process)
            try
            {
                if (process.ProcessName.StartsWith("XboxPcApp", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName is "ContentTestApp" or "XboxGameStreaming") pids.Add(process.Id);
            }
            catch (InvalidOperationException) { }
if (pids.Count == 0) { Console.Error.WriteLine("Start native Xbox Remote Play first, or select its process with --pid."); return 2; }
if (TraceEventSession.IsElevated() != true)
{ Console.Error.WriteLine("Creating this ETW diagnostic session requires an elevated terminal. No settings were changed."); return 3; }

var providers = new (string Name, Guid Id)[] {
    ("Microsoft.Streaming.Nano.Network", new("3f3c85cb-a328-4467-96df-438d76b4ac59")),
    ("Microsoft.Streaming.Nano.Network.urcp", new("e027bcec-71f3-4b85-ae28-90ff7446b675")),
    ("Microsoft.Streaming.Nano.Media", new("cb8b7862-eae7-4f30-9574-9453076ebaab")),
    ("Microsoft.Streaming.Nano", new("d20f61e9-8929-4282-9b24-b07db5a27a76")),
    ("Microsoft.Streaming.Basix", new("afc8a033-e28a-415c-acff-030e4b113a35")),
};
using var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
long bytesWritten = 0, omittedRecords = 0;
void Write(object record, bool force = false)
{
    string json = JsonSerializer.Serialize(record);
    int bytes = System.Text.Encoding.UTF8.GetByteCount(json) + 1;
    if (!force && bytesWritten + bytes > 16 * 1024 * 1024) { omittedRecords++; return; }
    writer.WriteLine(json); writer.Flush(); bytesWritten += bytes;
}
Write(new { kind = "scope", startedUtc = DateTimeOffset.UtcNow, seconds, processIds = pids.Order(), verbose,
    interpretation = "Experimental schema discovery. Field names and values have unverified units. No values are sent to Studio or used for timing adaptation." });
foreach (int pid in pids)
    try
    {
        using var process = Process.GetProcessById(pid);
        var modules = process.Modules.Cast<ProcessModule>()
            .Where(m => m.ModuleName.Contains("stream", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("nano", StringComparison.OrdinalIgnoreCase))
            .Take(32).Select(m => new { name = m.ModuleName, version = m.FileVersionInfo.FileVersion }).ToArray();
        Write(new { kind = "process", processId = pid, name = process.ProcessName, version = process.MainModule?.FileVersionInfo.FileVersion, streamingModules = modules });
    }
    catch (Exception error) { Write(new { kind = "processError", processId = pid, errorType = error.GetType().Name }); }
string localState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.GamingApp_8wekyb3d8bbwe", "LocalState");
try
{
    if (Directory.Exists(localState))
        foreach (string file in Directory.EnumerateFiles(localState, "gamestreaming_log*.txt").Take(8))
        {
            var info = new FileInfo(file);
            Write(new { kind = "logFile", name = info.Name, bytes = info.Length, modifiedUtc = info.LastWriteTimeUtc });
        }
}
catch (Exception error) { Write(new { kind = "logInventoryError", errorType = error.GetType().Name }); }
using var session = new TraceEventSession("AccessRelay-XboxStats-" + Guid.NewGuid().ToString("N"));
session.StopOnDispose = true;
var schemas = new HashSet<string>();
var lastSample = new Dictionary<string, long>();
var providerProcesses = new Dictionary<int, long>();
long matched = 0, metricSamples = 0, decodeErrors = 0;
session.Source.Dynamic.All += data =>
{
    if (providerProcesses.Count < 128 || providerProcesses.ContainsKey(data.ProcessID))
        providerProcesses[data.ProcessID] = providerProcesses.GetValueOrDefault(data.ProcessID) + 1;
    if (!pids.Contains(data.ProcessID)) return;
    matched++;
    string key = data.ProviderGuid + "/" + data.ID + "/" + data.Version + "/" + data.EventName;
    try
    {
        if (!schemas.Contains(key))
        {
            if (schemas.Count >= 256) return;
            schemas.Add(key);
            Write(new { kind = "schema", provider = data.ProviderName, eventName = data.EventName, id = (int)data.ID,
                version = data.Version, processId = data.ProcessID, fields = data.PayloadNames.Take(128).Select(n => n[..Math.Min(256, n.Length)]).ToArray() });
        }
        long now = Stopwatch.GetTimestamp();
        if (lastSample.TryGetValue(key, out long last) && Stopwatch.GetElapsedTime(last, now).TotalSeconds < 1) return;
        lastSample[key] = now;
        var fields = MetricFields.Read(data.PayloadNames.Take(128).Select((name, index) => new KeyValuePair<string, object?>(name, data.PayloadValue(index))));
        if (fields.Count == 0 || metricSamples >= 20000) return;
        metricSamples++;
        Write(new { kind = "numericFields", provider = data.ProviderName, eventName = data.EventName, processId = data.ProcessID,
            eventTimestampUtc = data.TimeStamp.ToUniversalTime(), eventRelativeMs = data.TimeStampRelativeMSec, observedQpc = now, fields });
    }
    catch (Exception) { decodeErrors++; }
};
foreach (var provider in providers)
    try
    {
        bool enabled = session.EnableProvider(provider.Id, verbose ? TraceEventLevel.Verbose : TraceEventLevel.Informational);
        Write(new { kind = "provider", provider.Name, provider.Id, enabled });
    }
    catch (Exception error) { Write(new { kind = "providerError", provider.Name, errorType = error.GetType().Name }); }
Console.WriteLine($"Observing Xbox processes {string.Join(", ", pids)} for {seconds} seconds. Keep Remote Play and its statistics overlay running.");
using var timeout = new Timer(_ => { try { session.Stop(); } catch (Exception) { } }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; try { session.Stop(); } catch (Exception) { } };
Console.CancelKeyPress += cancel;
try { session.Source.Process(); }
finally { Console.CancelKeyPress -= cancel; }
Write(new { kind = "summary", matched, metricSamples, schemas = schemas.Count, decodeErrors, eventsLost = session.Source.EventsLost,
    providerProcesses = providerProcesses.Take(128).Select(p => new { processId = p.Key, events = p.Value }).ToArray(), omittedRecords }, force: true);
Console.WriteLine($"Saved {schemas.Count} schemas and {metricSamples} numeric samples to {output}. Missing events do not prove the app has no counters.");
return 0;

static int Usage()
{
    Console.Error.WriteLine("Usage: StreamTelemetryProbe [--pid PID] [--seconds 5..120] [--verbose] [--output NEW_FILE.jsonl] | --self-test");
    return 1;
}
