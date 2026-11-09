using System.Runtime.InteropServices;
using EliteScriptStudio.Audio;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Output;

public interface IControllerOutput : IDisposable
{
    string Status { get; }
    bool IsAvailable { get; }
    bool TryWrite(ControllerState state);
    void Neutralize();
}

public static class VirtualOutput
{
    public const ushort VendorId = 0x045E;
    public const ushort ProductId = 0x028E;
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly string HostPath = Path.Combine(AppContext.BaseDirectory, "VirtualController", "AccessRelay.VirtualXboxHost.exe");

    public static string ProbeStatus()
    {
        try
        {
            return GetBlocker() ?? "USB/IP is ready. Start the virtual Xbox 360 controller to send output.";
        }
        catch (Exception error)
        {
            return $"Could not verify virtual output. {error.Message}";
        }
    }

    public static async Task<IControllerOutput> TryOpenAsync(CancellationToken cancellation = default)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (GetBlocker() is { } blocker)
                return new UnavailableOutput(blocker);

            // A stopped output detaches in the background. Wait before reusing its USB identity.
            await SessionGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await UsbipXboxOutput.OpenAsync(HostPath, VirtualMicDriver.UsbipExecutable,
                    () => SessionGate.Release(), cancellation).ConfigureAwait(false);
            }
            catch
            {
                SessionGate.Release();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new UnavailableOutput($"Could not start virtual Xbox output. {error.Message}");
        }
    }

    private static string? GetBlocker()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "Virtual Xbox output requires x64 Windows.";
        if (!File.Exists(HostPath) || !File.Exists(VirtualMicDriver.UsbipExecutable))
            return "The bundled virtual Xbox output files are missing. Open AccessRelay from its full release folder.";
        return VirtualMicDriver.IsAvailable ? null : "Install the bundled virtual device driver in Settings to start Xbox output.";
    }

    private sealed class UnavailableOutput(string status) : IControllerOutput
    {
        public string Status { get; } = status;
        public bool IsAvailable => false;
        public bool TryWrite(ControllerState state) => false;
        public void Neutralize() { }
        public void Dispose() { }
    }
}
