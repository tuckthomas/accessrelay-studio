using System.Diagnostics;

namespace EliteScriptStudio.Core;

/// <summary>Optional platform adapter owned by the scheduler. Deadlines use its injected clock.</summary>
internal interface IReleaseDeadlineTimer : IDisposable
{
    WaitHandle WaitHandle { get; }
    bool TrySchedule(long deadlineTimestamp);
    void Cancel();
}

/// <summary>A deadline wake-up source, never an independent controller-output writer.
/// Actual lateness is validated by the arbiter after reacquiring the runtime lock.</summary>
internal sealed class ReleaseScheduler : IDisposable
{
    private readonly Func<long?> _getDeadline;
    private readonly Action<long> _onDeadline;
    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _worker;
    private readonly IReleaseDeadlineTimer? _timer;
    private readonly WaitHandle[]? _timerWaits;
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private volatile bool _stopping;
    private int _disposeRequested;
    private bool _disposeOnWorkerExit;

    public ReleaseScheduler(Func<long?> getDeadline, Action<long> onDeadline,
        IReleaseDeadlineTimer? timer = null, Func<long>? timestamp = null, long timestampFrequency = 0)
    {
        _getDeadline = getDeadline;
        _onDeadline = onDeadline;
        _timer = timer;
        _timerWaits = timer is null ? null : [_changed, timer.WaitHandle];
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        if (_frequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        _worker = new Thread(Run) { IsBackground = true, Name = "AccessRelay release deadlines" };
        _worker.Start();
    }

    public void Signal()
    {
        if (_stopping) return;
        try { _changed.Set(); }
        catch (ObjectDisposedException) when (_stopping) { }
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                long? deadline = _getDeadline();
                if (deadline is null)
                {
                    _timer?.Cancel();
                    _changed.WaitOne();
                    continue;
                }
                double remainingMs = ((double)deadline.Value - _timestamp()) * 1000 / _frequency;
                if (remainingMs > 0)
                {
                    if (_timer is not null && _timer.TrySchedule(deadline.Value))
                        WaitHandle.WaitAny(_timerWaits!);
                    else
                    {
                        _timer?.Cancel();
                        _changed.WaitOne((int)Math.Clamp(Math.Ceiling(remainingMs), 1, 1000));
                    }
                    // An event, cancellation, replacement, or early timer signal always rechecks QPC.
                    continue;
                }
                _timer?.Cancel();
                _onDeadline(_timestamp());
            }
        }
        finally
        {
            _timer?.Cancel();
            if (_disposeOnWorkerExit) DisposeResources();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        _stopping = true;
        _changed.Set();
        if (Thread.CurrentThread == _worker) { _disposeOnWorkerExit = true; return; }
        _worker.Join();
        DisposeResources();
    }

    private void DisposeResources()
    {
        _timer?.Dispose();
        _changed.Dispose();
    }
}
