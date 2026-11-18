using System.Diagnostics;

namespace EliteScriptStudio.RemotePlay;

internal sealed class XboxRemoteConnection
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<GStreamerPeer> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly object _cancelGate = new();
    private bool _finished;
    private string _status = "Opening Xbox Remote Play…";
    private XboxTransportStats? _stats;
    internal string Status => Volatile.Read(ref _status);
    internal XboxTransportStats? Stats => Volatile.Read(ref _stats);
    private GStreamerPeer? _livePeer;
    internal GStreamerPeer? Peer => Volatile.Read(ref _livePeer);
    internal bool Ended => _worker.IsCompleted;
    internal bool Stopping => _stop.IsCancellationRequested || Ended;
    internal Task Completion => _worker;
    internal bool HadControllerAtEnd { get; private set; }
    internal Task<GStreamerPeer> Ready => _ready.Task;

    internal XboxRemoteConnection(XboxHomeService service, string console, nint playbackWindow)
    {
        _worker = Task.Run(async () =>
        {
            Trace("Opening Xbox Remote Play.", start: true);
            try { await RunAsync(service, console, playbackWindow, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            { Trace("Session cancelled."); Volatile.Write(ref _status, "Xbox Remote Play disconnected."); _ready.TrySetCanceled(); }
            catch (Exception e)
            { Trace("Session ended with " + e.GetType().Name + "."); Volatile.Write(ref _status, e.Message); _ready.TrySetException(e); }
            finally { lock (_cancelGate) { _finished = true; _stop.Dispose(); } }
        });
    }

    private static void Trace(string message, bool start = false)
    {
        // Only callers' stage names, states and counters belong here. SDP, addresses and sign-in data stay out of the log.
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Diagnostics");
            var path = Path.Combine(directory, "XboxRemotePlay.log");
            string line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            if (start)
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(path)) File.Move(path, Path.Combine(directory, "XboxRemotePlay.previous.log"), overwrite: true);
                File.WriteAllText(path, line);
            }
            else File.AppendAllText(path, line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string AnswerMedia(string answer, string kind)
    {
        string? line = answer.Split('\n').FirstOrDefault(s => s.StartsWith($"m={kind} ", StringComparison.Ordinal));
        return line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) switch
        { null => "absent", "0" => "rejected", _ => "accepted" };
    }

    private async Task RunAsync(XboxHomeService service, string console, nint playbackWindow, CancellationToken ct)
    {
        using var peer = new GStreamerPeer(true, message => Trace(message), playbackWindow);
        await using var session = await service.OpenAsync(console, ct);
        Trace("Home session provisioned.");
        Volatile.Write(ref _status, "Negotiating Xbox audio, video and input…");
        string offer = peer.CreateOffer(ct);
        string answer = await session.ExchangeSdpAsync(offer, ct);
        Trace($"SDP answer: audio {AnswerMedia(answer, "audio")}, video {AnswerMedia(answer, "video")}, data {AnswerMedia(answer, "application")}.");
        peer.AcceptAnswer(answer, ct);
        var local = await peer.LocalIceAsync(ct);
        peer.AddRemoteIce(await session.ExchangeIceAsync(local, ct, message => Trace(message)));
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connecting.CancelAfter(TimeSpan.FromSeconds(25));
        try { while (!peer.AdvanceHandshake()) await Task.Delay(30, connecting.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string status = $"The Xbox channel handshake timed out. {peer.TransportDescription()}";
            Trace(status);
            throw new TimeoutException(status);
        }
        Trace("Xbox data channels connected and authorized.");
        Volatile.Write(ref _status, "Xbox Remote Play connected.");
        Volatile.Write(ref _livePeer, peer);
        _ready.TrySetResult(peer);
        long sample = Stopwatch.GetTimestamp(), keepAlive = sample, lastReport = sample;
        Task? ping = null;
        bool videoStarted = false, audioStarted = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                peer.CheckTransport();
                peer.PullFrame();
                if (Stopwatch.GetElapsedTime(sample).TotalSeconds >= 1)
                {
                    var stats = peer.ReadStats(ct);
                    Volatile.Write(ref _stats, stats); sample = Stopwatch.GetTimestamp();
                    if (!videoStarted && stats.Width > 0 && stats.Height > 0)
                    { Trace($"Received video: {stats.Width}x{stats.Height}, decoder {stats.VideoDecoder}, {stats.VideoMemory} memory."); videoStarted = true; }
                    if (!audioStarted && stats.DecodedAudioBuffers > 0) { Trace("Received decoded Xbox audio."); audioStarted = true; }
                    if (Stopwatch.GetElapsedTime(lastReport).TotalSeconds >= 10)
                    {
                        static string Value(double? n) => n?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
                        string input = peer.HasControllerOutput && stats.ControllerWrites is { Samples: > 1 } writes
                            ? $"{writes.Accepted} controller writes, {stats.ControllerStateChanges} state changes, {writes.Rejected} rejected, local handoff p95 {Value(writes.P95Ms)} ms" : "no active controller measurements";
                        Trace($"Stream sample: {Value(stats.DecodedFps)} FPS, RTT {Value(stats.RttMs)} ms ({stats.RttSource ?? "unavailable"}), " +
                            $"jitter {Value(stats.JitterMs)} ms, jitter buffer {Value(stats.JitterBufferMs)} ms, decode {Value(stats.DecodeMs)} ms, " +
                            $"loss {Value(stats.PacketLossPercent)}%, {stats.RenderedFrames?.ToString() ?? "unknown"} frames rendered, " +
                            $"{stats.RenderDroppedFrames?.ToString() ?? "unknown"} dropped by player, {input}.");
                        lastReport = sample;
                    }
                }
                if (ping?.IsCompleted == true) { await ping; ping = null; }
                if (ping is null && Stopwatch.GetElapsedTime(keepAlive).TotalSeconds >= 20)
                { ping = session.KeepAliveAsync(ct); keepAlive = Stopwatch.GetTimestamp(); }
            }
        }
        finally
        {
            HadControllerAtEnd = peer.HasControllerOutput;
            Volatile.Write(ref _livePeer, null);
            peer.Dispose();
            if (ping is not null) try { await ping; } catch (HttpRequestException) { }
        }
    }

    internal void Cancel() { lock (_cancelGate) { if (!_finished) _stop.Cancel(); } }
}
