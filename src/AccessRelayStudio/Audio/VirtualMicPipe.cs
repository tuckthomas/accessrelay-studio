using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EliteScriptStudio.Audio;

internal sealed class VirtualMicPipe : IDisposable
{
    internal const string DeviceName = "AccessRelay Virtual Microphone";
    private static readonly string HelperDirectory = Path.Combine(AppContext.BaseDirectory, "VirtualMicrophone");
    private static readonly string HelperPath = Path.Combine(HelperDirectory, "AccessRelay.VirtualMicHost.exe");
    internal static string? SetupIssue { get; } = CheckSetup();
    private const int BlockMilliseconds = 10;
    private const int BlockSamples = VoiceSignal.SampleRate * BlockMilliseconds / 1000;
    private readonly Process _host;
    private readonly CancellationTokenSource _stop = new();
    private Task? _writer;
    private int _disposed;

    private VirtualMicPipe(Process host) => _host = host;

    public static async Task<VirtualMicPipe> OpenAsync(CancellationToken cancellation, int primeMilliseconds = 60)
    {
        if (SetupIssue is { } issue)
            throw new InvalidOperationException(issue);

        var start = new ProcessStartInfo(HelperPath)
        {
            WorkingDirectory = HelperDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(VirtualMicDriver.UsbipExecutable);
        start.Environment["ACCESSRELAY_MIC_PRIME_MS"] = primeMilliseconds.ToString(CultureInfo.InvariantCulture);
        var host = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the bundled virtual microphone.");
        var pipe = new VirtualMicPipe(host);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var errors = host.StandardError.ReadToEndAsync(deadline.Token);
            if (await host.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false) != "READY")
            {
                string reason = (await errors.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(reason.Length == 0 ? "Windows did not attach the virtual microphone." : reason);
            }
            return pipe;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new TimeoutException("Windows did not attach the virtual microphone in time.");
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static string? CheckSetup()
    {
        if (!File.Exists(HelperPath) || !File.Exists(VirtualMicDriver.UsbipExecutable))
            return "Virtual microphone files are missing. See the virtual device build instructions.";
        return VirtualMicDeviceIdentity.IsConfigured ? null
            : "Virtual microphone output requires a configured USB identity. See the virtual device build instructions.";
    }

    public void Start(VoiceSignal signal, Action<Exception> onFault) => _writer = Task.Run(async () =>
    {
        var samples = new float[BlockSamples];
        var bytes = new byte[samples.Length * sizeof(float)];
        try
        {
            var destination = _host.StandardInput.BaseStream;
            while (true)
            {
                // The USB capture callback owns the audio clock. Forward each batch as soon as it is ready.
                await signal.WaitForSamplesAsync(_stop.Token).ConfigureAwait(false);
                bool wrote = false;
                while (signal.TryReadBlock(samples))
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    await destination.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                    wrote = true;
                }
                if (wrote)
                    await destination.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested)
                onFault(error);
        }
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        try
        {
            if (_writer is null || _writer.Wait(250))
                _host.StandardInput.Close();
            if (!_host.WaitForExit(1000))
            {
                _host.Kill();
                _host.WaitForExit(500);
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            _host.Dispose();
            _stop.Dispose();
        }
    }
}
