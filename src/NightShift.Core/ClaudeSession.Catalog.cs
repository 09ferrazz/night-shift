using System.Diagnostics;
using System.Text.Json;
namespace NightShift.Core;

public sealed partial class ClaudeSession
{
    private SessionRecord? _origin;
    private DateTimeOffset _lastObservation;
    public async Task ConnectSharedAsync(ProcessStartInfo start,AppSettings settings)
    {
        await DisconnectAsync();await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed!=0,this);_baseStart=ClaudeLocator.Copy(start);_settings=settings;_operation=new();
            await DiscoverAsync(_operation.Token);IsShared=true;_connected=true;SetStatus("Escolha uma sessão Claude · somente leitura");
        }
        finally{_gate.Release();}
    }
    public Task ConnectSharedAsync(Uri endpoint,AppSettings settings)=>throw Unsupported("Conexão Claude por endpoint Codex");
    public async Task<IReadOnlyList<SessionRecord>> ListLiveSessionsAsync(string? project=null)
    {
        if(!IsConnected)throw new InvalidOperationException("Conecte a descoberta Claude primeiro.");
        var records=await DiscoverAsync(_operation.Token);
        return records.Where(r=>ClaudeCatalog.IsLive(r)&&(project is null||SessionCatalog.SameProject(r.ProjectPath,project))).ToArray();
    }
    private async Task<IReadOnlyList<SessionRecord>> DiscoverAsync(CancellationToken cancellation)
    {
        var result=await RunCommandAsync(["agents","--json","--all"],cancellation);
        if(result.ExitCode!=0)throw new NotSupportedException("Esta instalação não oferece Claude agents --json. Atualize o Claude Code para acompanhar sessões externas. "+result.Error);
        try{using var doc=JsonDocument.Parse(result.Output);return ClaudeCatalog.ReadLive(doc.RootElement);}
        catch(JsonException ex){throw new IOException("Claude agents retornou JSON inválido.",ex);}
    }
    public async Task<JsonElement> AttachLiveAsync(SessionRecord record)
    {
        if(!IsShared)throw new InvalidOperationException("Conecte a descoberta externa antes de vincular.");
        if(record.Provider!=AgentProvider.Claude)throw new InvalidOperationException("Esta conversa pertence a outro provedor.");
        await _gate.WaitAsync();
        try
        {
            if(Shift.Armed)throw new InvalidOperationException("Pare o acompanhamento antes de trocar de sessão.");
            var current=(await DiscoverAsync(_operation.Token)).SingleOrDefault(r=>r.ExternalId==record.ExternalId&&r.ThreadId==record.ThreadId&&r.ExternalKind==record.ExternalKind&&ClaudeCatalog.IsLive(r));
            if(current is null)throw new InvalidOperationException("Esta sessão não está mais ativa. Atualize a lista.");
            _origin=current;ThreadId=current.ThreadId;_project=current.ProjectPath;RestoredHistory=[];ApplyOrigin(current);_lastObservation=DateTimeOffset.UtcNow;
            Activity?.Invoke("Sessão Claude observada sem criar trabalhador nem alterar o transcript. Responda no terminal original.");
            return JsonSerializer.SerializeToElement(new{session=current,readOnly=true});
        }
        finally{_gate.Release();}
    }
    private void ApplyOrigin(SessionRecord record)
    {
        IsBusy=record.RuntimeStatus is "busy" or "working" or "waiting" or "blocked";
        WaitingInOrigin=record.RuntimeStatus is "waiting" or "blocked";
        SetStatus(WaitingInOrigin?"Precisa de você · terminal original":IsBusy?"Acompanhando Claude · somente leitura":"Claude aguardando · somente leitura");
    }
    private async Task RefreshObservationAsync(DateTimeOffset now)
    {
        if(!IsConnected||_origin is null||now-_lastObservation<TimeSpan.FromSeconds(3))return;
        if(!await _gate.WaitAsync(0))return;
        try
        {
            _lastObservation=now;
            if(Shift.Armed&&Shift.EndsAt<=now){Shift.Stop();Changed?.Invoke();}
            var records=await DiscoverAsync(_operation.Token);
            var current=records.FirstOrDefault(r=>r.ExternalId==_origin.ExternalId&&r.ThreadId==_origin.ThreadId&&r.ExternalKind==_origin.ExternalKind);
            if(current is null||!ClaudeCatalog.IsLive(current))
            {IsBusy=false;WaitingInOrigin=false;Shift.Stop();SetStatus("Sessão de origem encerrada · retome pelo histórico se desejar");return;}
            _origin=current;ApplyOrigin(current);
        }
        catch(OperationCanceledException){}
        catch(Exception ex) when(ex is IOException or NotSupportedException or TimeoutException)
        {Shift.Stop();IsBusy=false;WaitingInOrigin=false;SetStatus("Não foi possível revalidar a origem");Warning?.Invoke(ex.Message);}
        finally{_gate.Release();}
    }
    public async Task OpenOriginAsync()
    {
        if(!CanOpenOrigin||_origin is null)throw Unsupported("Abrir sessão interativa Claude");
        var origin=_origin;
        var current=(await DiscoverAsync(_operation.Token)).FirstOrDefault(r=>r.ExternalId==origin.ExternalId&&r.ThreadId==origin.ThreadId&&r.ExternalKind=="background");
        if(current is null)throw new InvalidOperationException("A sessão de origem não está disponível. Atualize a lista.");
        // An argument that begins with '-' must never become a CLI flag.
        if(!System.Text.RegularExpressions.Regex.IsMatch(current.ExternalId,"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$"))throw new IOException("Identificador Claude inválido para attach.");
        LaunchTerminal(["attach",current.ExternalId],current.ProjectPath);
    }
    private void LaunchTerminal(string[] arguments,string? cwd=null)
    {
        if(!OperatingSystem.IsWindows())throw Unsupported("Abrir terminal nativo nesta plataforma");
        var start=ClaudeLocator.Copy(_baseStart??throw new InvalidOperationException("Conecte o Claude primeiro."));
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        start.WorkingDirectory=Directory.Exists(cwd)?cwd!:Environment.CurrentDirectory;
        start.UseShellExecute=false;start.CreateNoWindow=false;
        // No pipes: Claude attach must inherit an interactive console, not become a managed stream.
        using var launched=Process.Start(start)??throw new IOException("Não foi possível abrir o terminal Claude.");
    }
    private async Task EnsureNotLiveAsync(string id,CancellationToken cancellation)
    {
        var live=await DiscoverAsync(cancellation);
        if(live.Any(r=>(r.ThreadId==id||r.ExternalId==id)&&ClaudeCatalog.IsLive(r)))
            throw new InvalidOperationException("Esta sessão Claude ainda está aberta na origem. Use o terminal original; o Night Shift não assume nem duplica uma sessão viva.");
    }
    public async Task<SessionPage> ListSessionsAsync(string? project=null,string? cursor=null,string? search=null)
    {
        if(cursor is not null)return new([],null);
        var native=ClaudeCatalog.ReadHistory();
        var cached=_store?.LoadSessions().Where(r=>r.Provider==AgentProvider.Claude)??[];
        var rows=cached.Concat(native).DistinctBy(r=>r.ThreadId).Where(r=>(project is null||SessionCatalog.SameProject(r.ProjectPath,project))&&
            (string.IsNullOrWhiteSpace(search)||r.Title.Contains(search,StringComparison.OrdinalIgnoreCase))).OrderByDescending(r=>r.UpdatedAt).Take(100).ToArray();
        await Task.CompletedTask;return new(rows,null);
    }
    public async Task<SessionRecord?> LatestSessionAsync(string project)=> (await ListSessionsAsync(project)).Sessions.FirstOrDefault();
    public Task<SessionRecord> ReadSessionAsync(string id)
    {
        var record=ReadHistorySnapshot(id);
        return Task.FromResult(record??throw new InvalidOperationException("Conversa Claude não encontrada no histórico local."));
    }
    private SessionRecord? ReadHistorySnapshot(string id)
    {
        try{return ClaudeCatalog.ReadRecord(id)??_store?.LoadSession(id,AgentProvider.Claude);}
        catch(IOException ex)
        {
            var cached=_store?.LoadSession(id,AgentProvider.Claude);
            if(cached is null)throw;
            Warning?.Invoke("Histórico nativo indisponível; exibindo o último cache salvo, que pode não incluir alterações recentes do terminal. "+ex.Message);
            return cached;
        }
    }
    public async Task<JsonElement> ResumeAsync(SessionRecord record)
    {
        if(IsShared)return await AttachLiveAsync(record);
        if(record.Provider!=AgentProvider.Claude)throw new InvalidOperationException("Esta conversa pertence a outro provedor.");
        if(!IsConnected)throw new InvalidOperationException("Conecte o Claude primeiro.");
        if(IsBusy||PendingRequests.Count>0)throw new InvalidOperationException("Pare o turno antes de retomar outra conversa.");
        if(!Directory.Exists(record.ProjectPath))throw new DirectoryNotFoundException("A pasta desta sessão Claude não existe.");
        if(string.IsNullOrWhiteSpace(record.ThreadId))throw new InvalidOperationException("Conversa Claude sem identificador.");
        var version=Volatile.Read(ref _stopVersion);await _gate.WaitAsync();
        try
        {
            await CloseWorkerAsync();
            await EnsureNotLiveAsync(record.ThreadId,_operation.Token);
            if(version!=Volatile.Read(ref _stopVersion))throw new OperationCanceledException();
            var history=(ReadHistorySnapshot(record.ThreadId)??record).Messages;
            Shift.Stop();_retryEligible=false;
            ThreadId=record.ThreadId;_sessionId=record.ThreadId;_project=Path.GetFullPath(record.ProjectPath);_resumeWorker=true;_firstPrompt=false;
            RestoredHistory=history;
            _items.Clear();SetStatus("Conversa Claude retomada · pronta para enviar");
            return JsonSerializer.SerializeToElement(new{session_id=ThreadId});
        }
        finally{_gate.Release();}
    }
    public async Task<JsonElement> AccountAsync()
    {
        var result=await RunCommandAsync(["auth","status"],_operation.Token);
        try
        {
            using var doc=JsonDocument.Parse(result.Output);var data=doc.RootElement;
            var loggedIn=data.TryGetProperty("loggedIn",out var logged)&&logged.ValueKind==JsonValueKind.True;
            return JsonSerializer.SerializeToElement(new{account=loggedIn?new{type="claude",email=CodexSession.Text(data,"email")}:null,loggedIn,authMethod=CodexSession.Text(data,"authMethod")});
        }
        catch(JsonException ex){throw new IOException("Claude auth status não retornou JSON. Faça login no terminal com claude auth login.",ex);}
    }
    public Task<JsonElement> LoginAsync()
    {
        if(IsBusy)throw new InvalidOperationException("Aguarde o Claude terminar antes de fazer login.");
        LaunchTerminal(["auth","login"]);return Task.FromResult(JsonSerializer.SerializeToElement(new{terminalStarted=true}));
    }
    public Task<IReadOnlyList<CodexModel>> ModelsAsync()
    {
        var models=new List<CodexModel>();
        if(_initialization.ValueKind==JsonValueKind.Object&&_initialization.TryGetProperty("models",out var data)&&data.ValueKind==JsonValueKind.Array)
            foreach(var row in data.EnumerateArray())
            {
                var value=CodexSession.Text(row,"value")??CodexSession.Text(row,"id");if(value is null)continue;
                var efforts=row.TryGetProperty("supportedEffortLevels",out var levels)&&levels.ValueKind==JsonValueKind.Array?levels.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToArray():[];
                models.Add(new(value,CodexSession.Text(row,"displayName")??value,efforts.Contains("medium")?"medium":efforts.FirstOrDefault()??"",efforts));
            }
        // Aliases are official CLI values. An empty list preserves local defaults when discovery is unavailable.
        return Task.FromResult<IReadOnlyList<CodexModel>>(models);
    }
    private async Task<(int ExitCode,string Output,string Error)> RunCommandAsync(string[] arguments,CancellationToken cancellation)
    {
        var start=ClaudeLocator.Copy(_baseStart??throw new InvalidOperationException("Conecte o Claude primeiro."));
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        start.UseShellExecute=false;start.CreateNoWindow=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;start.RedirectStandardInput=true;
        using var process=Process.Start(start)??throw new IOException("Não foi possível consultar Claude Code.");
        process.StandardInput.Close();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var output=process.StandardOutput.ReadToEndAsync(deadline.Token);var error=process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);return(process.ExitCode,await output,await error);
        }
        catch(OperationCanceledException)
        {
            if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();
            cancellation.ThrowIfCancellationRequested();throw new TimeoutException("Claude não respondeu à consulta em 15 segundos.");
        }
    }
}
