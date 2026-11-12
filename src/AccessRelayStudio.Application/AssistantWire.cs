using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteScriptStudio.Application;

public sealed record AssistantRequest(string Operation, JsonElement Arguments, int SchemaVersion = 1);
public sealed record AssistantReply(bool Ok, JsonElement? Result, string? Code = null, string? Message = null, int SchemaVersion = 1);
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
        var reply = AssistantJson.Read<AssistantReply>(await ReadAsync(pipe, timeout.Token));
        if (reply.SchemaVersion != 1) throw new AssistantException("protocolVersion", "Update the MCP bridge to match the running Studio installation.");
        return reply;
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
    private NamedPipeServerStream Create() => LocalAssistantPipe.Create(_name);
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
                    if (request.SchemaVersion != 1) throw new AssistantException("protocolVersion", "Update the MCP bridge to match the running Studio installation.");
                    using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    using var probeStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    // A connection carries one request. EOF (or extra bytes) cancels work immediately.
                    var probe = _listener.ReadAsync(new byte[1], probeStop.Token).AsTask();
                    var work = _dispatch(request, requestStop.Token);
                    object? result;
                    try
                    {
                        if (await Task.WhenAny(work, probe) == probe) requestStop.Cancel();
                        result = await work; requestStop.Token.ThrowIfCancellationRequested();
                    }
                    finally
                    {
                        probeStop.Cancel();
                        try { await probe; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
                    }
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
            finally
            {
                if (!_stop.IsCancellationRequested)
                { try { if (_listener.IsConnected) _listener.Disconnect(); } catch (IOException) { } }
            }
        }
    }
    public async ValueTask DisposeAsync()
    { _stop.Cancel(); _listener.Dispose(); try { await _worker; } catch (OperationCanceledException) { } _stop.Dispose(); }
}

static class LocalAssistantPipe
{
    internal static NamedPipeServerStream Create(string name)
    {
        if (name.Length is < 1 or > 120 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new AssistantException("pipeName", "Invalid local assistant pipe name.");
        if (!OperatingSystem.IsWindows()) return new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new IOException("Windows user identity is unavailable.");
        // Explicit owner/DACL plus PIPE_REJECT_REMOTE_CLIENTS: a normal named pipe can otherwise be reached over SMB.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{sid}D:P(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new IOException("Windows could not restrict assistant pipe access.");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, 0x3 | 0x40000000 | 0x80000, 0x8, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The private assistant endpoint is unavailable or already owned by another Studio instance."); }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out nint security, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
}
