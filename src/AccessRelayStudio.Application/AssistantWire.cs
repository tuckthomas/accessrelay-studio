using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EliteScriptStudio.Application;

public sealed record AssistantRequest(string Operation, JsonElement Arguments);
public sealed record AssistantReply(bool Ok, JsonElement? Result, string? Code = null, string? Message = null);
public static class AssistantWire
{
    public const int MaximumBytes = 1024 * 1024;
    public static string PipeName => "AccessRelayStudio.Mcp." + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..24];
    public static async Task WriteAsync(Stream stream, object value, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, AssistantJson.Options);
        if (bytes.Length > MaximumBytes) throw new AssistantException("messageTooLarge", "The assistant message exceeds its size limit.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation); await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }
    public static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > MaximumBytes) throw new AssistantException("messageTooLarge", "The assistant message exceeds its size limit.");
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, cancellation);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); return document.RootElement.Clone();
    }
    public static async Task<AssistantReply> CallAsync(string operation, object args, CancellationToken cancellation, string? pipeName = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(5000, timeout.Token); }
        catch (TimeoutException) { throw new AssistantException("studioUnavailable", "Open AccessRelay Studio and enable assistant access in Settings."); }
        await WriteAsync(pipe, new AssistantRequest(operation, JsonSerializer.SerializeToElement(args, AssistantJson.Options)), timeout.Token);
        return AssistantJson.Read<AssistantReply>(await ReadAsync(pipe, timeout.Token));
    }
}

public sealed class AssistantPipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly string _name;
    private readonly Func<AssistantRequest, CancellationToken, Task<object?>> _dispatch;
    private NamedPipeServerStream _listener;
    public AssistantPipeServer(Func<AssistantRequest, CancellationToken, Task<object?>> dispatch, string? pipeName = null)
    {
        _dispatch = dispatch; _name = pipeName ?? AssistantWire.PipeName;
        _listener = Create(); _worker = RunAsync();
    }
    private NamedPipeServerStream Create() => new(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _listener.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                AssistantReply reply;
                try
                {
                    var request = AssistantJson.Read<AssistantRequest>(await AssistantWire.ReadAsync(_listener, deadline.Token));
                    object? result = await _dispatch(request, deadline.Token);
                    reply = new(true, JsonSerializer.SerializeToElement(result, AssistantJson.Options));
                }
                catch (AssistantException error) { reply = new(false, null, error.Code, error.Message); }
                catch (OperationCanceledException) { reply = new(false, null, "cancelled", "The assistant request was cancelled or timed out."); }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
                { reply = new(false, null, "invalidRequest", "The assistant request is malformed or outside its bounds."); }
                catch (Exception) { reply = new(false, null, "requestFailed", "The request could not finish. Inspect its state in Studio before retrying."); }
                await AssistantWire.WriteAsync(_listener, reply, deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { _listener.Dispose(); }
            if (!_stop.IsCancellationRequested) _listener = Create();
        }
    }
    public async ValueTask DisposeAsync()
    { _stop.Cancel(); _listener.Dispose(); try { await _worker; } catch (OperationCanceledException) { } _stop.Dispose(); }
}
