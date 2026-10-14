using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;

namespace NightShift.Core;

public sealed class RpcException(int code, string message, JsonElement data) : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement DataElement { get; } = data;
}

public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly ClientWebSocket? _socket;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _life = new();
    private readonly Task _reader;
    private readonly Task _stderr;
    private long _sequence;
    private int _disposed;
    private volatile bool _ended;
    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? Diagnostic;
    public event Action<string>? Disconnected;
    public bool IsAlive => !_ended && !_life.IsCancellationRequested && (_socket is not null ? _socket.State == WebSocketState.Open : _process is not null && !_process.HasExited);

    private JsonRpcClient(ClientWebSocket socket)
    {
        _socket = socket; _stderr = Task.CompletedTask; _reader = Task.Run(ReadLoopAsync);
    }
    public static async Task<JsonRpcClient> ConnectWebSocketAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        SharedConnection.ParseWebSocket(endpoint.AbsoluteUri);
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null; // Loopback connections must not travel through the user's HTTP proxy.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await socket.ConnectAsync(endpoint, timeout.Token); return new(socket); }
        catch { socket.Dispose(); throw; }
    }

    public JsonRpcClient(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = start.StandardErrorEncoding = new UTF8Encoding(false);
        start.StandardInputEncoding = new UTF8Encoding(false);
        _process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o Codex.");
        _reader = Task.Run(ReadLoopAsync);
        _stderr = Task.Run(async () =>
        {
            try { while (await _process.StandardError.ReadLineAsync(_life.Token) is { } line) Diagnostic?.Invoke(line[..Math.Min(line.Length, 2000)]); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        });
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _sequence);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = source;
        using var budget = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken, _life.Token);
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, linked.Token);
            return await source.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_life.IsCancellationRequested)
        { throw new TimeoutException("O Codex demorou demais para responder a " + method + "."); }
        finally { _pending.TryRemove(key, out _); }
    }
    public Task NotifyAsync(string method, object? parameters = null) => WriteAsync(new { method, @params = parameters }, _life.Token);
    public Task ReplyAsync(JsonElement id, object result) => WriteAsync(new { id, result }, _life.Token);
    public Task ReplyErrorAsync(JsonElement id, string message) => WriteAsync(new { id, error = new { code = -32601, message } }, _life.Token);
    private async Task WriteAsync(object payload, CancellationToken cancellationToken)
    {
        if (!IsAlive) throw new IOException("A conexão com o Codex foi encerrada.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(payload);
            if (_socket is not null) await _socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
            else
            {
                await _process!.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
        }
        finally { _writeGate.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (await ReadMessageAsync() is { } line)
            {
                if (line.Length > 16 * 1024 * 1024) throw new IOException("Mensagem do Codex excedeu o tamanho aceito.");
                JsonElement m;
                try { using var document = JsonDocument.Parse(line); m = document.RootElement.Clone(); }
                catch (JsonException) { Diagnostic?.Invoke("Uma linha fora do protocolo foi ignorada."); continue; }
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (m.TryGetProperty("id", out var id) && !m.TryGetProperty("method", out _))
                {
                    var key = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
                    if (_pending.TryRemove(key, out var source))
                    {
                        if (m.TryGetProperty("error", out var error)) source.TrySetException(new RpcException(
                            error.TryGetProperty("code", out var c) && c.TryGetInt32(out var number) ? number : -1,
                            error.TryGetProperty("message", out var text) ? text.GetString() ?? "Erro do Codex." : "Erro do Codex.", error.Clone()));
                        else if (m.TryGetProperty("result", out var result)) source.TrySetResult(result.Clone());
                        else source.TrySetException(new IOException("Resposta JSON-RPC sem resultado."));
                    }
                }
                else MessageReceived?.Invoke(m);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or WebSocketException)
        { if (!_life.IsCancellationRequested) Diagnostic?.Invoke(ex.Message); }
        finally
        {
            _ended = true;
            foreach (var item in _pending.Values) item.TrySetException(new IOException("O processo Codex encerrou a conexão."));
            if (!_life.IsCancellationRequested) Disconnected?.Invoke("O Codex foi desconectado. Reconecte para retomar sua sessão.");
        }
    }
    private async Task<string?> ReadMessageAsync()
    {
        if (_socket is null) return await _process!.StandardOutput.ReadLineAsync(_life.Token);
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var part = await _socket.ReceiveAsync(buffer.AsMemory(), _life.Token);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text) throw new IOException("O servidor enviou uma mensagem que não é JSON textual.");
            if (message.Length + part.Count > 16 * 1024 * 1024) throw new IOException("Mensagem do Codex excedeu o tamanho aceito.");
            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _life.Cancel();
        foreach (var source in _pending.Values) source.TrySetException(new IOException("Conexão encerrada."));
        _socket?.Abort();
        try { if (_process is not null) { _process.StandardInput.Close(); if (!_process.HasExited) _process.Kill(true); } } catch (InvalidOperationException) { }
        try { await Task.WhenAll(_reader, _stderr).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { }
        _socket?.Dispose(); _process?.Dispose(); _life.Dispose();
    }
}
