using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteScriptStudio;

internal sealed class PrecisionTimer : WaitHandle
{
    private const uint HighResolution = 2;
    private const uint ModifyAndSynchronize = 0x00100002;

    public static PrecisionTimer? TryStart(int intervalMs)
    {
        var handle = CreateWaitableTimerEx(0, 0, HighResolution, ModifyAndSynchronize);
        long due = -intervalMs * 10_000L;
        if (handle.IsInvalid || !SetWaitableTimer(handle, ref due, intervalMs, 0, 0, false))
        {
            handle.Dispose();
            return null;
        }
        return new PrecisionTimer { SafeWaitHandle = handle };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", ExactSpelling = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        nint completionRoutine, nint context, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
