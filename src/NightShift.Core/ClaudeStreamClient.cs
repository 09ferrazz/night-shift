using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace NightShift.Core;

/// <summary>Native Claude SDK control channel. User content is sent only over stdin JSONL.</summary>
internal sealed class ClaudeStreamClient : IAsyncDisposable
{
    // Replay includes base64 image blocks: support the same finite attachment budget as the composer.
    private const long MaximumLineCharacters=AttachmentStore.MaxAttachments*AttachmentStore.MaxFileBytes*4L/3+8*1024*1024;
    private readonly Process _process;
    private readonly CancellationTokenSource _life=new();
    private readonly SemaphoreSlim _writer=new(1,1);
    private readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> _controls=new();
    private readonly ConcurrentDictionary<string,TaskCompletionSource<bool>> _acceptance=new();
    private readonly Task _reader;
    private readonly Task _stderr;
    private int _closed;
    public event Action<JsonElement>? Message;
    public event Action<string>? Diagnostic;
    public event Action<string>? Disconnected;
    public bool IsAlive => Volatile.Read(ref _closed)==0&&!_process.HasExited;
    public ClaudeStreamClient(ProcessStartInfo start)
    {
        start.UseShellExecute=false;start.CreateNoWindow=true;
        start.RedirectStandardInput=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        start.StandardInputEncoding=new UTF8Encoding(false);start.StandardOutputEncoding=new UTF8Encoding(false,true);start.StandardErrorEncoding=new UTF8Encoding(false);
        _process=Process.Start(start)??throw new IOException("Não foi possível iniciar Claude Code.");
        _reader=Task.Run(ReadAsync);_stderr=Task.Run(ReadErrorAsync);
    }
    public async Task<JsonElement> ControlAsync(object request,CancellationToken cancellation=default)
    {
        var id=Guid.NewGuid().ToString("N");var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _controls[id]=completion;
        try
        {
            await WriteAsync(new{type="control_request",request_id=id,request},cancellation);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15),cancellation);
        }
        finally{_controls.TryRemove(id,out _);}
    }
    public async Task<bool> SendAsync(string sessionId,object[] content,CancellationToken cancellation)
    {
        var id=Guid.NewGuid().ToString();var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptance[id]=completion;
        try
        {
            await WriteAsync(new {type="user",uuid=id,session_id=sessionId,parent_tool_use_id=(string?)null,message=new {role="user",content}},cancellation);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15),cancellation);
        }
        finally{_acceptance.TryRemove(id,out _);}
    }
    public async Task WriteAsync(object value,CancellationToken cancellation=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(_life.Token,cancellation);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        await _writer.WaitAsync(linked.Token);
        try
        {
            if(!IsAlive)throw new IOException("Claude Code desconectou.");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(),linked.Token);
            await _process.StandardInput.FlushAsync(linked.Token);
        }
        finally{_writer.Release();}
    }
    private async Task ReadAsync()
    {
        string reason="Claude Code encerrou a conexão.";
        try
        {
            var buffer=new char[4096];var pending=new StringBuilder();int count;
            while((count=await _process.StandardOutput.ReadAsync(buffer.AsMemory(),_life.Token))>0)
            {
                for(int i=0;i<count;i++)
                {
                    if(buffer[i]=='\n'){ParseLine(pending.ToString());pending.Clear();}
                    else pending.Append(buffer[i]);
                    if(pending.Length>MaximumLineCharacters)throw new IOException("Evento Claude excedeu o limite de tamanho.");
                }
            }
            if(pending.Length>0)ParseLine(pending.ToString());
        }
        catch(OperationCanceledException){reason="Conexão encerrada.";}
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or DecoderFallbackException){reason="Falha no protocolo Claude: "+ex.Message;}
        finally{Fail(reason);}
    }
    private void ParseLine(string line)
    {
        line=line.Trim();if(line.Length==0)return;
        if(!line.StartsWith('{')){Diagnostic?.Invoke(line.Length>1000?line[..1000]:line);return;}
        using var document=JsonDocument.Parse(line);var message=document.RootElement;
        if(CodexSession.Text(message,"type")=="control_response" && message.TryGetProperty("response",out var reply))
        {
            var id=CodexSession.Text(reply,"request_id");
            if(id is not null&&_controls.TryRemove(id,out var waiting))
            {
                if(CodexSession.Text(reply,"subtype")=="error")waiting.TrySetException(new NotSupportedException(CodexSession.Text(reply,"error")??"Controle Claude não disponível."));
                else waiting.TrySetResult(reply.TryGetProperty("response",out var result)?result.Clone():JsonSerializer.SerializeToElement(new{}));
            }
            return;
        }
        if(CodexSession.Text(message,"type")=="user")
        {
            var id=CodexSession.Text(message,"uuid");
            if(id is not null&&_acceptance.TryGetValue(id,out var accepted))accepted.TrySetResult(true);
        }
        // Versions that omit a replay UUID may still emit a start or a result for accepted input.
        if(CodexSession.Text(message,"type") is "stream_event" or "assistant" or "result" or "control_request")
            foreach(var accepted in _acceptance.Values)accepted.TrySetResult(true);
        Message?.Invoke(message.Clone());
    }
    private async Task ReadErrorAsync()
    {
        try {while(await _process.StandardError.ReadLineAsync(_life.Token) is {} line)Diagnostic?.Invoke(line.Length>1000?line[..1000]:line);}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
    }
    private void Fail(string reason)
    {
        if(Interlocked.Exchange(ref _closed,1)!=0)return;
        _life.Cancel();
        foreach(var waiting in _controls.Values)waiting.TrySetException(new IOException(reason));
        foreach(var waiting in _acceptance.Values)waiting.TrySetException(new IOException(reason));
        _controls.Clear();_acceptance.Clear();Disconnected?.Invoke(reason);
        try{if(!_process.HasExited)_process.Kill(true);}catch(InvalidOperationException){}
    }
    public void CancelPending()
    {
        foreach(var waiting in _controls.Values)waiting.TrySetCanceled();
        foreach(var waiting in _acceptance.Values)waiting.TrySetCanceled();
    }
    public async ValueTask DisposeAsync()
    {
        // Keep the native transcript flush opportunity before terminating an owned child.
        Interlocked.Exchange(ref _closed,1);_life.Cancel();CancelPending();
        try{_process.StandardInput.Close();}catch(IOException){}
        try{await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));}
        catch(TimeoutException)
        {
            try{if(!_process.HasExited)_process.Kill(true);}catch(InvalidOperationException){}
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Task.WhenAll(_reader,_stderr);_process.Dispose();_life.Dispose();
    }
}
