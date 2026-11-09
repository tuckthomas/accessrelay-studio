using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace EliteScriptStudio.Audio;

internal sealed record VoiceModelOption(string Name, string Description, string? Package = null, string? Executable = null,
    int OutputPrimeMilliseconds = 20, string? ModelFile = null)
{
    private static readonly VoiceModelOption[] Catalog =
    [
        new("Built-in effects", "Adjust pitch and tone with the original real-time presets."),
        new("MeanVC2", "Streaming reference-voice conversion with the 120 ms model and 40 ms of lookahead.",
            "MeanVC2", "PlaySignal.MeanVC2Host.exe", OutputPrimeMilliseconds: 200,
            ModelFile: "meanvc2-120ms-40ms-q4_k.gguf"),
        new("MioCodec V2", "Local reference-voice conversion in 160 ms blocks. Requires a Vulkan-capable GPU.",
            "MioCodec", "PlaySignal.MioCodecHost.exe", ModelFile: "miocodec-25hz-44khz-v2-q8_0.gguf")
    ];

    public bool UsesReference => Executable is not null;
    public string PathOnDisk => Path.Combine(AppContext.BaseDirectory, "NeuralVoice", Package ?? "");
    public bool IsAvailable => !UsesReference ||
        File.Exists(Path.Combine(PathOnDisk, Executable!)) &&
        File.Exists(Path.Combine(PathOnDisk, "audiocpp.dll")) &&
        ModelFile is { } model && File.Exists(Path.Combine(PathOnDisk, model));
    public static VoiceModelOption[] Installed() => Catalog.Where(model => model.IsAvailable).ToArray();
}

internal sealed class NeuralVoiceWorker : IDisposable
{
    private const int MaxSamples = VoiceSignal.SampleRate;
    private const int BlockSamples = VoiceSignal.SampleRate * 160 / 1000;
    private const int InputQueueCapacity = 2;
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<float[]> _input = Channel.CreateBounded<float[]>(new BoundedChannelOptions(InputQueueCapacity)
        { SingleWriter = true, SingleReader = true });
    private float[] _pending = new float[BlockSamples];
    private Task? _processing;
    private int _filled;
    private int _disposed;

    private NeuralVoiceWorker(Process process, int outputPrimeMilliseconds)
    {
        _process = process;
        OutputPrimeMilliseconds = outputPrimeMilliseconds;
    }

    public int OutputPrimeMilliseconds { get; }
    public bool IsInputQueueFull => _input.Reader.Count >= InputQueueCapacity;

    public static async Task<NeuralVoiceWorker> OpenAsync(VoiceModelOption model, string reference, CancellationToken cancellation)
    {
        if (!model.UsesReference || !model.IsAvailable)
            throw new InvalidOperationException("The selected AI model package is missing.");
        if (!File.Exists(reference))
            throw new InvalidOperationException("The voice profile’s reference clip is missing. Import the clip again.");

        var start = new ProcessStartInfo(Path.Combine(model.PathOnDisk, model.Executable!))
        {
            WorkingDirectory = model.PathOnDisk,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--reference");
        start.ArgumentList.Add(reference);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the AI voice host.");
        var worker = new NeuralVoiceWorker(process, model.OutputPrimeMilliseconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            var errors = process.StandardError.ReadToEndAsync();
            string? ready = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (ready != "READY")
            {
                string detail = (await errors.WaitAsync(deadline.Token).ConfigureAwait(false)).Trim();
                if (detail.Length > 500)
                    detail = detail[^500..];
                throw new InvalidOperationException(detail.Length > 0 ? detail : "The AI voice host did not start.");
            }
            return worker;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            worker.Dispose();
            throw new TimeoutException("The AI voice model did not finish loading in time.");
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    public bool Capture(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            int count = Math.Min(samples.Length, BlockSamples - _filled);
            samples[..count].CopyTo(_pending.AsSpan(_filled));
            samples = samples[count..];
            _filled += count;
            if (_filled < BlockSamples)
                continue;
            if (!_input.Writer.TryWrite(_pending))
                return false;
            _pending = new float[BlockSamples];
            _filled = 0;
        }
        return true;
    }

    public void Start(Action<float[], int> onSamples, Action<Exception> onFault)
    {
        CancellationToken stop = _stop.Token;
        _processing = Task.Run(() => ProcessAudioAsync(onSamples, onFault, stop));
    }

    private async Task ProcessAudioAsync(Action<float[], int> onSamples, Action<Exception> onFault, CancellationToken stop)
    {
        try
        {
            var packets = new AudioPackets(_process);
            await foreach (var block in _input.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                int count = await packets.ConvertAsync(block, stop).ConfigureAwait(false);
                if (count > 0)
                    onSamples(packets.Samples, count);
            }
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            onFault(error is OperationCanceledException ? new TimeoutException("The AI model stopped returning audio.") : error);
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }

    public async Task<float[]> ConvertPreviewAsync(float[] samples, CancellationToken stop)
    {
        var packets = new AudioPackets(_process);
        var converted = new float[samples.Length];
        int written = 0;
        for (int offset = 0; offset < samples.Length; offset += BlockSamples)
        {
            int count = Math.Min(BlockSamples, samples.Length - offset);
            await AppendAsync(samples.AsMemory(offset, count)).ConfigureAwait(false);
        }

        // Let streaming models emit their lookahead and queued output after the last speech block.
        const int flushBlocks = 3;
        for (int index = 0; index < flushBlocks && written < converted.Length; index++)
            await AppendAsync(ReadOnlyMemory<float>.Empty).ConfigureAwait(false);
        if (written != converted.Length)
            throw new InvalidDataException("The AI model did not return a complete preview.");
        return converted;

        async Task AppendAsync(ReadOnlyMemory<float> block)
        {
            int count = await packets.ConvertAsync(block, stop).ConfigureAwait(false);
            count = Math.Min(count, converted.Length - written);
            packets.Samples.AsSpan(0, count).CopyTo(converted.AsSpan(written));
            written += count;
        }
    }

    private sealed class AudioPackets(Process process)
    {
        private readonly Stream _input = process.StandardInput.BaseStream;
        private readonly Stream _output = process.StandardOutput.BaseStream;
        private readonly byte[] _request = new byte[BlockSamples * sizeof(float)];
        private readonly byte[] _header = new byte[sizeof(int)];
        private readonly byte[] _response = new byte[MaxSamples * sizeof(float)];

        public float[] Samples { get; } = new float[MaxSamples];

        public async Task<int> ConvertAsync(ReadOnlyMemory<float> block, CancellationToken stop)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var timeLimit = deadline.Token;
            MemoryMarshal.AsBytes(block.Span).CopyTo(_request);
            await _input.WriteAsync(_request, timeLimit).ConfigureAwait(false);
            await _input.FlushAsync(timeLimit).ConfigureAwait(false);
            await _output.ReadExactlyAsync(_header, timeLimit).ConfigureAwait(false);
            int count = BinaryPrimitives.ReadInt32LittleEndian(_header);
            if (count is < 0 or > MaxSamples)
                throw new InvalidDataException("The AI model returned an invalid audio block.");
            int bytes = count * sizeof(float);
            await _output.ReadExactlyAsync(_response.AsMemory(0, bytes), timeLimit).ConfigureAwait(false);
            Buffer.BlockCopy(_response, 0, Samples, 0, bytes);
            return count;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _input.Writer.TryComplete();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally
        {
            _processing?.Wait(TimeSpan.FromMilliseconds(500));
            _process.Dispose();
            _stop.Dispose();
        }
    }
}
