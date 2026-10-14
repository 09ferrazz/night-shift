using System.Diagnostics;
using System.Text.Json;
namespace NightShift.Core;

public sealed partial class ClaudeSession : IAgentSession
{
    private readonly AppStore? _store;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _state=new();
    private readonly List<PendingRequest> _pending=[];
    private readonly Dictionary<string,ChatEntry> _items=new();
    private ClaudeStreamClient? _client;
    private ProcessStartInfo? _baseStart;
    private AppSettings _settings=new();
    private CancellationTokenSource _operation=new();
    private string _project="",_workerProject="",_workerModel="",_workerEffort="",_sessionId=Guid.NewGuid().ToString();
    private bool _assistantSeen,_workerAuto,_connected,_resumeWorker,_firstPrompt=true,_quotaRejected,_nonTimerFailure,_retryEligible;
    private DateTimeOffset? _quotaReset;
    private string? _streamId;
    private JsonElement _initialization;
    private int _sending,_stopVersion,_turnVersion,_disposed;
    public ClaudeSession(AppStore? store=null){_store=store;}
    public bool IsManualOperation => false;
    public DateTimeOffset? RatesUpdatedAt {get;private set;}
    public string? ThreadId {get;private set;}
    public bool IsConnected => _connected;
    public bool IsBusy {get;private set;}
    public bool CanSteer => false;
    public bool IsPreparing => Volatile.Read(ref _sending)>0;
    public int TurnsStarted {get;private set;}
    public IReadOnlyList<ChatEntry> RestoredHistory {get;private set;}=[];
    public ShiftState Shift {get;}=new();
    public RateSnapshot Rates {get;private set;}=new();
    public string Status {get;private set;}="Desconectado";
    public IReadOnlyList<PendingRequest> PendingRequests {get{lock(_state)return _pending.ToArray();}}
    public bool IsShared {get;private set;}
    public bool IsReadOnly => IsShared;
    public bool WaitingInOrigin {get;private set;}
    public bool CanAcceptSharedInput => false;
    public bool ContinuationReady => false;
    public bool CanOpenOrigin => IsShared&&_origin?.ExternalKind=="background";
    public event Action? Changed;
    public event Action<ChatEntry>? ChatChanged;
    public event Action<string>? Activity;
    public event Action<string>? Warning;

    public async Task ConnectAsync(ProcessStartInfo start,AppSettings settings)
    {
        await DisconnectAsync();await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed!=0,this);_baseStart=ClaudeLocator.Copy(start);_settings=settings;IsShared=false;
            _operation=new();_connected=true;
            _project=Directory.Exists(settings.ProjectPath)?Path.GetFullPath(settings.ProjectPath):"";
            await StartWorkerAsync(_project.Length>0?_project:Environment.CurrentDirectory,settings.ClaudeModel,settings.ClaudeEffort,_operation.Token);
            SetStatus("Claude conectado");
        }
        catch{_connected=false;await CloseWorkerAsync();SetStatus("Não foi possível conectar ao Claude");throw;}
        finally{_gate.Release();}
    }
    public void UpdateSettings(AppSettings settings)=>_settings=settings;
    private async Task StartWorkerAsync(string project,string model,string effort,CancellationToken cancellation)
    {
        await CloseWorkerAsync();cancellation.ThrowIfCancellationRequested();
        var start=ClaudeLocator.Copy(_baseStart??throw new InvalidOperationException("Conecte o Claude primeiro."));
        start.WorkingDirectory=project;
        foreach(var arg in new[]{"-p","--input-format","stream-json","--output-format","stream-json","--verbose","--include-partial-messages","--replay-user-messages","--permission-prompt-tool","stdio","--permission-mode",_settings.AutoReview?"auto":"default"})start.ArgumentList.Add(arg);
        start.ArgumentList.Add((_resumeWorker?"--resume=":"--session-id=")+_sessionId);
        if(!string.IsNullOrWhiteSpace(model))start.ArgumentList.Add("--model="+model);
        if(!string.IsNullOrWhiteSpace(effort))start.ArgumentList.Add("--effort="+effort);
        _workerProject=project;_workerModel=model;_workerEffort=effort;_workerAuto=_settings.AutoReview;
        var client=new ClaudeStreamClient(start);_client=client;
        client.Message+=m=>{if(ReferenceEquals(_client,client))HandleMessage(m);};
        client.Diagnostic+=m=>{if(ReferenceEquals(_client,client))Activity?.Invoke("Claude: "+m);};
        client.Disconnected+=reason=> {
            if(!ReferenceEquals(_client,client))return;
            _connected=false;IsBusy=false;_retryEligible=false;Shift.Stop();ClearPending();SetStatus("Claude desconectado");Warning?.Invoke(reason);
        };
        try{_initialization=await client.ControlAsync(new{subtype="initialize",hooks=new{}},cancellation);}
        catch(Exception ex) when(ex is IOException or TimeoutException or NotSupportedException)
        {
            throw new IOException("Claude não confirmou o protocolo oficial. Atualize o Claude Code. Se o modo automático não estiver disponível, desative Auto Review e conecte novamente. Nenhuma permissão foi contornada.",ex);
        }
    }
    private async Task CloseWorkerAsync()
    {
        var client=_client;_client=null;
        if(client is not null)await client.DisposeAsync();
    }
    public void StartShift(int hours)
    {
        if(!IsConnected)throw new InvalidOperationException("Conecte o Claude primeiro.");
        Shift.Arm(DateTimeOffset.UtcNow,hours);Changed?.Invoke();
    }
    public async Task<bool> SendAsync(string project,string text,string model,string effort,IReadOnlyList<Attachment>? attachments=null)
    {
        if(IsShared)throw Unsupported("Enviar mensagens a uma sessão externa");
        if(string.IsNullOrWhiteSpace(text)&&attachments?.Count is not >0)return false;
        AttachmentInput.Validate(attachments);
        if(!Directory.Exists(project))throw new DirectoryNotFoundException("Selecione uma pasta de projeto válida.");
        var version=Volatile.Read(ref _stopVersion);Interlocked.Increment(ref _sending);Changed?.Invoke();
        await _gate.WaitAsync();
        try
        {
            if(version!=Volatile.Read(ref _stopVersion))return false;
            if(!IsConnected)throw new InvalidOperationException("Conecte o Claude primeiro.");
            if(IsBusy||PendingRequests.Count>0)throw new InvalidOperationException("Aguarde o Claude terminar ou responda à solicitação.");
            var full=Path.GetFullPath(project);
            if(ThreadId is not null&&!SessionCatalog.SameProject(_project,full))throw new InvalidOperationException("Inicie uma nova sessão antes de trocar a pasta.");
            _project=full;_settings.ClaudeModel=model;_settings.ClaudeEffort=effort;
            return await SendLockedAsync(text,model,effort,attachments,version,false);
        }
        finally{Interlocked.Decrement(ref _sending);_gate.Release();Changed?.Invoke();}
    }
    private async Task<bool> SendLockedAsync(string text,string model,string effort,IReadOnlyList<Attachment>? attachments,int version,bool retry)
    {
        if(version!=Volatile.Read(ref _stopVersion))return false;
        var cancellation=_operation.Token;AttachmentInput.Validate(attachments);
        var content=await BuildInputAsync(_firstPrompt?Protocol.Preamble+"\n\nTarefa do usuário:\n"+text:text,attachments,cancellation);
        if(_client?.IsAlive!=true||!SessionCatalog.SameProject(_workerProject,_project)||model!=_workerModel||effort!=_workerEffort||_settings.AutoReview!=_workerAuto)
        {
            // Retire the adapter-owned idle process before checking for other live owners.
            await CloseWorkerAsync();
            if(ThreadId is not null)await EnsureNotLiveAsync(ThreadId,cancellation);
            if(version!=Volatile.Read(ref _stopVersion))return false;
            await StartWorkerAsync(_project,model,effort,cancellation);
        }
        if(version!=Volatile.Read(ref _stopVersion))return false;
        Shift.Complete();_retryEligible=false;_quotaRejected=false;_nonTimerFailure=false;_quotaReset=null;_streamId=null;_assistantSeen=false;_turnVersion=version;IsBusy=true;SetStatus("Trabalhando");
        try
        {
            var accepted=await _client!.SendAsync(_sessionId,content,cancellation);
            if(!accepted||version!=Volatile.Read(ref _stopVersion))return false;
            ThreadId=_sessionId;_resumeWorker=true;_firstPrompt=false;TurnsStarted++;
            ChatChanged?.Invoke(new(){Role=retry?"Night Shift":"Você",Text=text,Attachments=attachments?.ToList()??[]});Changed?.Invoke();return true;
        }
        catch(OperationCanceledException) when(version!=Volatile.Read(ref _stopVersion)){return false;}
        catch
        {
            // stdin may have reached Claude even when acceptance did not arrive. Retire only this owned worker,
            // invalidate late events and require explicit reconnection/history review before another send.
            _connected=false;CancelOperation();await CloseWorkerAsync();IsBusy=false;
            SetStatus("Envio incerto · confira o histórico antes de reenviar");
            Warning?.Invoke("Claude não confirmou o envio e pode ter executado a tarefa. O trabalhador gerenciado foi encerrado; rascunho preservado. Confira o histórico antes de reconectar e reenviar.");
            throw;
        }
    }
    private static async Task<object[]> BuildInputAsync(string text,IReadOnlyList<Attachment>? attachments,CancellationToken cancellation)
    {
        if(attachments?.Count is not >0)return [new{type="text",text}];
        var manifest=(JsonElement)JsonSerializer.SerializeToElement(AttachmentInput.Build(text,attachments)[0]);
        var content=new List<object>{new{type="text",text=manifest.GetProperty("text").GetString()}};
        foreach(var image in attachments.Where(a=>a.IsImage))
        {
            var media=Path.GetExtension(image.Path).ToLowerInvariant() switch{".png"=>"image/png",".webp"=>"image/webp",".jpg" or ".jpeg"=>"image/jpeg",_=>throw new NotSupportedException("Formato de imagem Claude não suportado.")};
            var bytes=await File.ReadAllBytesAsync(image.Path,cancellation);
            if(bytes.LongLength!=image.Length||bytes.LongLength>AttachmentStore.MaxFileBytes)throw new IOException("A imagem foi alterada. Anexe novamente: "+image.Name);
            content.Add(new{type="image",source=new{type="base64",media_type=media,data=Convert.ToBase64String(bytes)}});
        }
        return content.ToArray();
    }
    private void HandleMessage(JsonElement message)
    {
        var type=CodexSession.Text(message,"type");
        if(type is "assistant" or "stream_event" or "result" or "rate_limit_event" or "control_request" && _turnVersion!=Volatile.Read(ref _stopVersion))return;
        if(type=="system"&&CodexSession.Text(message,"subtype")=="init")
        {
            var id=CodexSession.Text(message,"session_id");
            if(id is not null&&id!=_sessionId)throw new IOException("Claude respondeu com outra sessão. O rascunho foi preservado.");
        }
        else if(type=="control_request")HandleControl(message);
        else if(type=="control_cancel_request")
        {
            var id=CodexSession.Text(message,"request_id");lock(_state)_pending.RemoveAll(r=>r.Id.ValueKind==JsonValueKind.String&&r.Id.GetString()==id);Changed?.Invoke();
        }
        else if(type=="rate_limit_event"&&message.TryGetProperty("rate_limit_info",out var info))ApplyQuota(info);
        else if(type=="stream_event"&&message.TryGetProperty("event",out var ev))
        {
            if(CodexSession.Text(ev,"type")=="message_start"&&ev.TryGetProperty("message",out var start))_streamId=CodexSession.Text(start,"id")??Guid.NewGuid().ToString("N");
            if(CodexSession.Text(ev,"type")=="content_block_delta"&&ev.TryGetProperty("delta",out var delta)&&CodexSession.Text(delta,"type")=="text_delta")
                UpdateText(_streamId??=Guid.NewGuid().ToString("N"),CodexSession.Text(delta,"text")??"",true);
        }
        else if(type=="assistant")
        {
            if(CodexSession.Text(message,"error") is "rate_limit" or "rate_limit_error")_quotaRejected=true;
            if(CodexSession.Text(message,"error") is "billing_error" or "authentication_failed")_nonTimerFailure=true;
            if(message.TryGetProperty("message",out var body))
            {
                var text=ClaudeCatalog.ReadText(body);
                if(text.Length>0)UpdateText(CodexSession.Text(body,"id")??_streamId??Guid.NewGuid().ToString("N"),text,false);
            }
        }
        else if(type=="result")
        {
            IsBusy=false;ClearPending();
            var failed=message.TryGetProperty("is_error",out var error)&&error.ValueKind==JsonValueKind.True;
            var code=CodexSession.Text(message,"errorCode");
            if(code is "rate_limit" or "rate_limit_error")_quotaRejected=true;
            if(code is "credits_required" or "billing_error")_nonTimerFailure=true;
            _retryEligible=failed&&_quotaRejected&&!_nonTimerFailure;
            if(_retryEligible)
            {
                Shift.WaitForQuota(_quotaReset is {} reset&&reset>DateTimeOffset.UtcNow?reset.AddSeconds(15):DateTimeOffset.UtcNow.AddMinutes(5));
                SetStatus(Shift.Armed?"Aguardando limite":"Limite atingido");
                Activity?.Invoke(_quotaReset is null?"Limite rejeitado. Reset não informado; nova tentativa em 5 minutos quando o turno estiver ativo.":"Limite rejeitado; aguardando reset informado pelo Claude.");
            }
            else
            {
                Shift.Complete();
                if(!failed&&!_assistantSeen&&CodexSession.Text(message,"result") is {} result&&result.Length>0)UpdateText(Guid.NewGuid().ToString("N"),result,false);
                SetStatus(failed?"Precisa de atenção":"Pronto");
                if(failed)Warning?.Invoke("Claude encerrou o turno com erro. Confira a atividade ou o terminal; esta falha não autoriza continuação automática.");
            }
            Changed?.Invoke();
        }
    }
    private void UpdateText(string id,string text,bool append)
    {
        _assistantSeen=true;
        if(!_items.TryGetValue(id,out var entry))_items[id]=entry=new(){Id=id,Role="Claude"};
        entry.Text=append?entry.Text+text:text;ChatChanged?.Invoke(entry);
    }
    private void ApplyQuota(JsonElement info)
    {
        Rates=RateSnapshot.ParseClaude(info);RatesUpdatedAt=DateTimeOffset.UtcNow;
        if(CodexSession.Text(info,"errorCode")=="credits_required")_nonTimerFailure=true;
        if(CodexSession.Text(info,"status")=="rejected"&&!_nonTimerFailure)_quotaRejected=true;
        if(info.TryGetProperty("resetsAt",out var value)&&value.TryGetInt64(out var seconds))
            try{_quotaReset=DateTimeOffset.FromUnixTimeSeconds(seconds);}catch(ArgumentOutOfRangeException){}
        Changed?.Invoke();
    }
    private void HandleControl(JsonElement message)
    {
        if(!message.TryGetProperty("request_id",out var id)||id.ValueKind!=JsonValueKind.String||!message.TryGetProperty("request",out var request))throw new IOException("Solicitação Claude sem identificador.");
        if(CodexSession.Text(request,"subtype")!="can_use_tool")
        {
            _=RejectUnknownControlAsync(id.Clone());return;
        }
        _retryEligible=false;_quotaRejected=false;Shift.Complete();
        var input=request.TryGetProperty("input",out var parameters)?parameters.Clone():JsonSerializer.SerializeToElement(new{});
        var tool=CodexSession.Text(request,"tool_name")??"Ferramenta desconhecida";
        var pending=new PendingRequest(id.Clone(),tool=="AskUserQuestion"?"claude/question":"claude/permission",tool=="AskUserQuestion"?input:JsonSerializer.SerializeToElement(new{toolName=tool,input,
            permissionSuggestions=request.TryGetProperty("permission_suggestions",out var suggestions)?suggestions.Clone():JsonSerializer.SerializeToElement(Array.Empty<object>()),decisionReason=CodexSession.Text(request,"decision_reason")}));
        lock(_state)if(!_pending.Any(p=>p.Key==pending.Key))_pending.Add(pending);
        SetStatus("Precisa de você");Activity?.Invoke("Claude aguarda: "+tool);
    }
    private async Task RejectUnknownControlAsync(JsonElement id)
    {
        var client=_client;if(client is null)return;
        try{await client.WriteAsync(new{type="control_response",response=new{subtype="error",request_id=id,error="Solicitação de controle não suportada pelo Night Shift."}},_operation.Token);}
        catch(Exception ex) when(ex is IOException or OperationCanceledException){Activity?.Invoke(ex.Message);}
    }
    public async Task ResolveRequestAsync(PendingRequest request,object? result)
    {
        if(IsShared)throw Unsupported("Responder solicitações externas");
        var version=Volatile.Read(ref _stopVersion);
        await _gate.WaitAsync();
        try
        {
            if(version!=Volatile.Read(ref _stopVersion)||!PendingRequests.Any(p=>ReferenceEquals(p,request)))return;
            var reply=JsonSerializer.SerializeToElement(result);object decision;
            if(request.Method=="claude/question"&&reply.ValueKind==JsonValueKind.Object&&reply.TryGetProperty("answers",out var answers))
            {
                var input=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(request.Parameters.GetRawText())??new();input["answers"]=answers.Clone();
                decision=new{behavior="allow",updatedInput=input};
            }
            else if(CodexSession.Text(reply,"behavior")=="allow")
                decision=new{behavior="allow",updatedInput=reply.TryGetProperty("updatedInput",out var updated)?updated.Clone():request.Parameters.GetProperty("input").Clone()};
            else decision=new{behavior="deny",message=CodexSession.Text(reply,"message")??"O usuário recusou esta solicitação."};
            var client=_client??throw new IOException("Claude desconectou.");
            await client.WriteAsync(new{type="control_response",response=new{subtype="success",request_id=request.Id,response=decision}},_operation.Token);
            if(version!=Volatile.Read(ref _stopVersion))return;
            lock(_state)_pending.Remove(request);SetStatus(PendingRequests.Count>0?"Precisa de você":IsBusy?"Trabalhando":"Pronto");
        }
        finally{_gate.Release();}
    }
    public async Task CheckRetryAsync(DateTimeOffset now,Func<ContinuationDraft>? draftProvider=null)
    {
        if(IsShared){await RefreshObservationAsync(now);return;}
        if(Shift.Armed&&Shift.EndsAt<=now){await StopAsync();return;}
        if(!IsConnected||!_retryEligible||!Shift.CanRetry(now)||IsBusy||PendingRequests.Count>0)return;
        if(!await _gate.WaitAsync(0))return;
        ContinuationDraft? draft=null;
        try
        {
            var version=Volatile.Read(ref _stopVersion);
            if(!_retryEligible||!Shift.CanRetry(now)||IsBusy||PendingRequests.Count>0||ThreadId is null)return;
            draft=draftProvider?.Invoke();
            if(version!=Volatile.Read(ref _stopVersion)||!Shift.Armed)return;
            if(await SendLockedAsync(Protocol.ContinueWithDraft(draft?.Text),_settings.ClaudeModel,_settings.ClaudeEffort,draft?.Attachments,version,true))draft?.Accepted();
        }
        catch(OperationCanceledException){}
        catch(Exception ex) when(ex is IOException or InvalidOperationException or NotSupportedException or TimeoutException)
        {_retryEligible=false;Shift.Complete();Warning?.Invoke("Retomada não aceita; rascunho preservado: "+ex.Message);}
        finally{draft?.Finished?.Invoke();_gate.Release();Changed?.Invoke();}
    }
    public Task RefreshQuotaAsync()
    {
        Activity?.Invoke(IsShared?"A descoberta externa Claude não publica limites ou resets.":"Os limites Claude são atualizados somente pelos eventos nativos do turno. Dados ausentes ficam Não informado.");return Task.CompletedTask;
    }
    public async Task StopAsync()
    {
        CancelOperation();
        await _gate.WaitAsync();
        try
        {
            if(!IsShared&&_client?.IsAlive==true)
            {
                using var stopDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try{await _client.ControlAsync(new{subtype="interrupt"},stopDeadline.Token);}
                catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException or NotSupportedException){Activity?.Invoke("Interrupção nativa indisponível; encerrando o trabalhador gerenciado.");}
            }
            await CloseWorkerAsync();_operation.Dispose();_operation=new();IsBusy=false;WaitingInOrigin=false;Shift.Stop();_retryEligible=false;ClearPending();
            if(IsShared){_connected=false;_origin=null;ThreadId=null;}
            SetStatus(IsConnected?"Parado":"Desconectado");
        }
        finally{_gate.Release();}
    }
    private void CancelOperation()
    {
        Interlocked.Increment(ref _stopVersion);Shift.Stop();_retryEligible=false;_operation.Cancel();_client?.CancelPending();ClearPending();
    }
    public async Task DisconnectAsync()
    {
        CancelOperation();await _gate.WaitAsync();
        try
        {
            _connected=false;await CloseWorkerAsync();IsBusy=false;WaitingInOrigin=false;IsShared=false;_origin=null;
            ThreadId=null;RestoredHistory=[];_items.Clear();_sessionId=Guid.NewGuid().ToString();_resumeWorker=false;_firstPrompt=true;_project="";
            Rates=new();RatesUpdatedAt=null;SetStatus("Desconectado");
        }
        finally{_gate.Release();}
    }
    public async Task NewSessionAsync()
    {
        if(IsShared)throw Unsupported("Criar conversa na origem externa");
        if(IsBusy||PendingRequests.Count>0)throw new InvalidOperationException("Pare o turno antes de criar uma conversa.");
        CancelOperation();await _gate.WaitAsync();
        try{await CloseWorkerAsync();_operation=new();ThreadId=null;RestoredHistory=[];_items.Clear();_sessionId=Guid.NewGuid().ToString();_resumeWorker=false;_firstPrompt=true;SetStatus("Nova sessão Claude");}
        finally{_gate.Release();}
    }
    private void ClearPending(){lock(_state)_pending.Clear();Changed?.Invoke();}
    private void SetStatus(string status){Status=status;Changed?.Invoke();}
    private static NotSupportedException Unsupported(string action)=>new(action+" não está disponível no conector Claude. Use o terminal original.");
    public Task<JsonElement> SetupSandboxAsync()=>throw Unsupported("Configurar sandbox Windows Codex");
    public Task ForkAsync()=>throw Unsupported("Fork");
    public Task ReviewAsync()=>throw Unsupported("Review");
    public Task CompactAsync()=>throw Unsupported("Compact");
    public Task RenameAsync(string name)=>throw Unsupported("Rename");
    public Task<string> McpStatusAsync()=>throw Unsupported("MCP");
    public Task ConfirmSharedContinuationAsync(Func<ContinuationDraft>? draftProvider=null)=>throw Unsupported("Continuar sessão externa");
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;await DisconnectAsync();_operation.Dispose();
    }
}
