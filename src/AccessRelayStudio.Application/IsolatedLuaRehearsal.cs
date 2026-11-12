using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteScriptStudio.Application;

public sealed class IsolatedLuaRehearsal(string executable, string? managedAssembly = null) : ILuaRehearsal
{
    public async Task<LuaSimulationResult> RunAsync(LuaSimulationRequest request, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (managedAssembly is not null) start.ArgumentList.Add(managedAssembly);
        start.ArgumentList.Add("--lua-worker");
        using var process = Process.Start(start) ?? throw new AssistantException("workerUnavailable", "The Lua worker could not start.");
        using var job = OperatingSystem.IsWindows() ? WorkerJob.Limit(process) : null;
        async Task DrainErrors()
        { var buffer = new char[1024]; while (await process.StandardError.ReadAsync(buffer, limit.Token) != 0) { } }
        var errors = DrainErrors();
        var monitor = Task.Run(async () =>
        {
            while (!process.HasExited && !limit.IsCancellationRequested)
            {
                process.Refresh();
                if (process.WorkingSet64 > 384L * 1024 * 1024 || process.TotalProcessorTime > TimeSpan.FromSeconds(5)) limit.Cancel();
                await Task.Delay(50, limit.Token);
            }
        }, limit.Token);
        try
        {
            await AssistantWire.WriteAsync(process.StandardInput.BaseStream, request, limit.Token);
            process.StandardInput.Close();
            var result = AssistantJson.Read<LuaSimulationResult>(await AssistantWire.ReadAsync(process.StandardOutput.BaseStream, limit.Token));
            await process.WaitForExitAsync(limit.Token); await errors;
            if (process.ExitCode != 0) throw new AssistantException("rehearsalFailed", "The isolated Lua worker rejected this rehearsal.");
            return result;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        { throw new AssistantException("rehearsalStopped", "Lua rehearsal was cancelled or exceeded its process/resource limits."); }
        finally
        {
            limit.Cancel(); if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            try { await monitor; } catch (OperationCanceledException) { }
            try { await errors; } catch (Exception error) when (error is OperationCanceledException or IOException) { }
        }
    }
}

/// <summary>Windows enforces a process memory quota and kills the worker on job closure.</summary>
sealed class WorkerJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private WorkerJob() : base(true) { }
    internal static WorkerJob Limit(Process process)
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) { job.Dispose(); process.Kill(entireProcessTree: true); throw new AssistantException("workerIsolation", "Windows could not isolate the rehearsal worker."); }
        var info = new Extended { Basic = new() { Flags = 0x2000 | 0x100 | 0x8, ActiveProcessLimit = 1 }, ProcessMemoryLimit = 384U * 1024 * 1024 };
        if (!SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<Extended>()) || !AssignProcessToJobObject(job, process.Handle))
        { job.Dispose(); process.Kill(entireProcessTree: true); throw new AssistantException("workerIsolation", "Windows could not enforce rehearsal process limits."); }
        return job;
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern WorkerJob CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(WorkerJob job, int kind, ref Extended info, int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(WorkerJob job, nint process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(nint handle);
}
