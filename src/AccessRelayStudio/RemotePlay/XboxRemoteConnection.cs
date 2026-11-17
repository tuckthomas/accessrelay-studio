using System.Diagnostics;

namespace EliteScriptStudio.RemotePlay;

internal sealed class XboxRemoteConnection
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<GStreamerPeer> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private string _status = "Opening Xbox Remote Play…";
    private XboxTransportStats? _stats;
    internal string Status => Volatile.Read(ref _status);
    internal XboxTransportStats? Stats => Volatile.Read(ref _stats);
    private GStreamerPeer? _livePeer;
    internal GStreamerPeer? Peer => Volatile.Read(ref _livePeer);
    internal bool Ended => _worker.IsCompleted;
    internal Task<GStreamerPeer> Ready => _ready.Task;

    internal XboxRemoteConnection(XboxHomeService service, string console)
    {
        _worker = Task.Run(async () =>
        {
            try { await RunAsync(service, console, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            { Volatile.Write(ref _status, "Xbox Remote Play disconnected."); _ready.TrySetCanceled(); }
            catch (Exception e)
            { Volatile.Write(ref _status, e.Message); _ready.TrySetException(e); }
        });
    }

    private async Task RunAsync(XboxHomeService service, string console, CancellationToken ct)
    {
        using var peer = new GStreamerPeer();
        await using var session = await service.OpenAsync(console, ct);
        Volatile.Write(ref _status, "Negotiating Xbox audio, video and input…");
        string offer = peer.CreateOffer(ct);
        string answer = await session.ExchangeSdpAsync(offer, ct);
        peer.AcceptAnswer(answer, ct);
        var local = await peer.LocalIceAsync(ct);
        peer.AddRemoteIce(await session.ExchangeIceAsync(local, ct));
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connecting.CancelAfter(TimeSpan.FromSeconds(25));
        while (!peer.AdvanceHandshake()) await Task.Delay(30, connecting.Token);
        Volatile.Write(ref _status, "Xbox Remote Play connected.");
        Volatile.Write(ref _livePeer, peer);
        _ready.TrySetResult(peer);
        long sample = Stopwatch.GetTimestamp(), keepAlive = sample;
        Task? ping = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                peer.CheckTransport();
                peer.PullFrame();
                if (Stopwatch.GetElapsedTime(sample).TotalSeconds >= 1)
                {
                    Volatile.Write(ref _stats, peer.ReadStats(ct)); sample = Stopwatch.GetTimestamp();
                }
                if (ping?.IsCompleted == true) { await ping; ping = null; }
                if (ping is null && Stopwatch.GetElapsedTime(keepAlive).TotalSeconds >= 20)
                { ping = session.KeepAliveAsync(ct); keepAlive = Stopwatch.GetTimestamp(); }
            }
        }
        finally
        {
            Volatile.Write(ref _livePeer, null);
            peer.Dispose();
            if (ping is not null) try { await ping; } catch (HttpRequestException) { }
        }
    }

    internal void Cancel() => _stop.Cancel();
    internal async Task CloseAsync()
    {
        Cancel(); await _worker; _stop.Dispose();
    }
}
