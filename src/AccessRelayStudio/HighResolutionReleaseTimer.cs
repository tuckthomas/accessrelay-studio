using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using Microsoft.Win32.SafeHandles;

namespace EliteScriptStudio;

/// <summary>Windows high-resolution one-shot timer. It improves the wait source, not the
/// scheduling guarantee; the arbiter still checks actual QPC lateness before releasing.</summary>
internal sealed class HighResolutionReleaseTimer : WaitHandle, IReleaseDeadlineTimer
{
    private const uint HighResolution = 2;
    private const uint ModifyAndSynchronize = 0x00100002;
    private HighResolutionReleaseTimer(SafeWaitHandle handle) => SafeWaitHandle = handle;
    WaitHandle IReleaseDeadlineTimer.WaitHandle => this;

    internal static HighResolutionReleaseTimer? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var handle = CreateWaitableTimerEx(0, 0, HighResolution, ModifyAndSynchronize);
            if (!handle.IsInvalid) return new HighResolutionReleaseTimer(handle);
            handle.Dispose();
            return null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return null; }
    }

    public bool TrySchedule(long deadlineTimestamp)
    {
        double remaining = (double)deadlineTimestamp - Stopwatch.GetTimestamp();
        double hundredNanoseconds = Math.Ceiling(remaining * 10_000_000 / Stopwatch.Frequency);
        if (!double.IsFinite(hundredNanoseconds) || hundredNanoseconds >= long.MaxValue) return false;
        // Negative due times are relative; period=0 creates a one-shot rather than a periodic wake.
        long due = -(long)Math.Max(1, hundredNanoseconds);
        return SetWaitableTimer(SafeWaitHandle, ref due, 0, 0, 0, false);
    }

    public void Cancel() => CancelWaitableTimer(SafeWaitHandle);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        nint completionRoutine, nint context, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
