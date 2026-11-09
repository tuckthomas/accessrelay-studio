using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Output;

internal sealed class UsbipXboxOutput : IControllerOutput
{
    internal const int ReportLength = 12;
    private readonly object _gate = new();
    private readonly Process _host;
    private readonly Task<string> _diagnostics;
    private readonly Action _release;
    private readonly record struct QueuedReport(ControllerState State, long EnqueuedAt);

    private readonly Channel<QueuedReport> _reports = Channel.CreateBounded<QueuedReport>(
        new BoundedChannelOptions(32) { SingleReader = true });
    private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ControllerState? _last;
    private string _status = "Virtual Xbox 360 controller connected over USB/IP.";
    private bool _failed;
    private bool _disposed;
    private bool _drain;
    private long _lastHandoffAt;
    private double _lastHandoffMs;

    private UsbipXboxOutput(Process host, Task<string> diagnostics, Action release)
    {
        _host = host;
        _diagnostics = diagnostics;
        _release = release;
        _ = Task.Run(RunAsync);
    }

    public string Status { get { lock (_gate) return _status; } }
    public bool IsAvailable { get { lock (_gate) return !_failed && !_disposed; } }
    internal double? RecentHandoffMs
    {
        get
        {
            long completed = Volatile.Read(ref _lastHandoffAt);
            return completed != 0 && Stopwatch.GetElapsedTime(completed) <= TimeSpan.FromSeconds(3)
                ? Volatile.Read(ref _lastHandoffMs) : null;
        }
    }

    internal static async Task<UsbipXboxOutput> OpenAsync(string hostPath, string usbipPath, Action release,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(hostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(usbipPath);
        var host = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the bundled Xbox output host.");
        var diagnostics = ReadDiagnosticsAsync(host.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (await host.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) != "READY")
            {
                string reason = await ReadReasonAsync(diagnostics).ConfigureAwait(false);
                throw new InvalidOperationException(reason.Length == 0 ? "Windows did not open the virtual Xbox controller." : reason);
            }
            cancellation.ThrowIfCancellationRequested();
            return new UsbipXboxOutput(host, diagnostics, release);
        }
        catch (Exception error)
        {
            try { await CloseHostAsync(host).ConfigureAwait(false); }
            finally { host.Dispose(); }
            if (error is OperationCanceledException && !cancellation.IsCancellationRequested)
                throw new TimeoutException("Windows did not open the virtual Xbox controller in time.", error);
            throw;
        }
    }

    public bool TryWrite(ControllerState state)
    {
        lock (_gate)
        {
            if (_failed || _disposed)
                return false;
            if (_last == state)
                return true;
            if (!_reports.Writer.TryWrite(new(state, Stopwatch.GetTimestamp())))
            {
                FailLocked("Virtual Xbox output could not keep up with input reports.");
                return false;
            }
            _last = state;
            return true;
        }
    }

    public void Neutralize() => TryWrite(default);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _drain = !_failed && (_last == default(ControllerState) ||
                _reports.Writer.TryWrite(new(default, Stopwatch.GetTimestamp())));
            if (!_failed)
                _status = "Virtual Xbox output stopped.";
            _reports.Writer.TryComplete();
            _closing.TrySetResult();
        }
    }

    internal static void EncodeReport(ControllerState state, Span<byte> report)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(report, state.Buttons);
        report[2] = state.LeftTrigger;
        report[3] = state.RightTrigger;
        BinaryPrimitives.WriteInt16LittleEndian(report[4..], state.LeftX);
        BinaryPrimitives.WriteInt16LittleEndian(report[6..], state.LeftY);
        BinaryPrimitives.WriteInt16LittleEndian(report[8..], state.RightX);
        BinaryPrimitives.WriteInt16LittleEndian(report[10..], state.RightY);
    }

    private async Task RunAsync()
    {
        using var stopWriter = new CancellationTokenSource();
        try
        {
            Task writer = WriteReportsAsync(stopWriter.Token);
            Task exited = _host.WaitForExitAsync();
            if (await Task.WhenAny(writer, exited, _closing.Task).ConfigureAwait(false) == exited)
            {
                string reason = await ReadReasonAsync(_diagnostics).ConfigureAwait(false);
                Fail(reason.Length == 0 ? "Virtual Xbox output disconnected." : $"Virtual Xbox output stopped. {reason}");
            }

            bool drain;
            lock (_gate) drain = _drain;
            if (drain && !writer.IsCompleted && !exited.IsCompleted)
                await Task.WhenAny(writer, Task.Delay(150)).ConfigureAwait(false);

            stopWriter.Cancel();
            if (!writer.IsCompleted && !exited.IsCompleted)
                await Task.WhenAny(writer, Task.Delay(100)).ConfigureAwait(false);
            if (!writer.IsCompleted && !exited.IsCompleted)
                TryKill(_host);
            await CloseHostAsync(_host).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Fail($"Virtual Xbox output stopped. {error.Message}");
            TryKill(_host);
        }
        finally
        {
            _host.Dispose();
            _release();
        }
    }

    private async Task WriteReportsAsync(CancellationToken cancellation)
    {
        try
        {
            var pipe = _host.StandardInput.BaseStream;
            var bytes = new byte[ReportLength];
            await foreach (QueuedReport report in _reports.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
            {
                cancellation.ThrowIfCancellationRequested();
                EncodeReport(report.State, bytes);
                await pipe.WriteAsync(bytes, cancellation).ConfigureAwait(false);
                await pipe.FlushAsync(cancellation).ConfigureAwait(false);
                long completed = Stopwatch.GetTimestamp();
                Volatile.Write(ref _lastHandoffMs, Stopwatch.GetElapsedTime(report.EnqueuedAt, completed).TotalMilliseconds);
                Volatile.Write(ref _lastHandoffAt, completed);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { Fail($"Virtual Xbox output stopped. {error.Message}"); }
    }

    private void Fail(string reason)
    {
        lock (_gate) FailLocked(reason);
    }

    private void FailLocked(string reason)
    {
        if (_failed || _disposed)
            return;
        _failed = true;
        _status = reason;
        _reports.Writer.TryComplete();
        _closing.TrySetResult();
    }

    private static async Task CloseHostAsync(Process host)
    {
        try { host.StandardInput.Dispose(); }
        catch (IOException) { }
        var exit = host.WaitForExitAsync();
        if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false) != exit)
        {
            TryKill(host);
            await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
    }

    private static void TryKill(Process host)
    {
        try { if (!host.HasExited) host.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<string> ReadReasonAsync(Task<string> diagnostics) =>
        await Task.WhenAny(diagnostics, Task.Delay(200)).ConfigureAwait(false) == diagnostics
            ? await diagnostics.ConfigureAwait(false) : string.Empty;

    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader)
    {
        const int limit = 1024;
        var tail = new StringBuilder();
        var buffer = new char[256];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > limit)
                    tail.Remove(0, tail.Length - limit);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return tail.ToString().Trim();
    }
}
