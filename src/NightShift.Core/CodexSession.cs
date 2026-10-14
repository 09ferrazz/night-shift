using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace NightShift.Core;

public sealed record PendingRequest(JsonElement Id, string Method, JsonElement Parameters)
{
    public string Key => Id.GetRawText();
    public string Title => Method switch
    {
        "claude/permission" => "Claude precisa de permissão",
        "claude/question" => "Claude precisa de uma resposta",
        "item/commandExecution/requestApproval" => "Permissão para executar comando",
        "item/fileChange/requestApproval" => "Permissão para alterar arquivos",
        "item/tool/requestUserInput" => "Codex precisa de uma resposta",
        "item/permissions/requestApproval" => "Permissão de acesso",
        "mcpServer/elicitation/request" => "Solicitação de uma integração",
        _ => "Solicitação não reconhecida"
    };
    public string Details => Parameters.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String
        ? cmd.GetString()! : Parameters.GetRawText();
}

public sealed partial class CodexSession : IAgentSession
{
    private JsonRpcClient? _rpc;
    private AppSettings _settings = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<(int Connection, JsonElement Message)> _events = Channel.CreateUnbounded<(int, JsonElement)>(new() { SingleReader = true });
    private readonly CancellationTokenSource _life = new();
    private readonly Task _eventLoop;
    private readonly Dictionary<string, ChatEntry> _items = new();
    private readonly List<PendingRequest> _pending = [];
    private readonly object _pendingGate = new();
    private string _project = "";
    private string? _turn;
    private string? _loadingThreadId;
    private volatile bool _forking;
    private string? _forkParentThreadId;
    public bool IsManualOperation { get; private set; }
    private bool _firstPrompt;
    private bool _usageFailure;
    private int _stopVersion;
    private int _sending;
    private int _connectionVersion;
    private int _disposed;
    public DateTimeOffset? RatesUpdatedAt { get; private set; }
    public string? ThreadId { get; private set; }
    public bool IsConnected => _rpc?.IsAlive == true;
    public bool IsBusy { get; private set; }
    public bool CanSteer => IsConnected && IsBusy && _turn is not null && !IsManualOperation && PendingRequests.Count == 0 &&
        (!IsShared || CanAcceptSharedInput && !WaitingInOrigin);
    public bool IsPreparing => Volatile.Read(ref _sending) > 0;
    public int TurnsStarted { get; private set; }
    public IReadOnlyList<ChatEntry> RestoredHistory { get; private set; } = [];
    public ShiftState Shift { get; } = new();
    public RateSnapshot Rates { get; private set; } = new();
    public string Status { get; private set; } = "Desconectado";
    public IReadOnlyList<PendingRequest> PendingRequests { get { lock (_pendingGate) return _pending.ToArray(); } }
    public event Action? Changed;
    public event Action<ChatEntry>? ChatChanged;
    public event Action<string>? Activity;
    public event Action<string>? Warning;
    public CodexSession() { _eventLoop = Task.Run(ProcessEventsAsync); }

    public Task ConnectAsync(ProcessStartInfo start, AppSettings settings) => ConnectCoreAsync(() => Task.FromResult(new JsonRpcClient(start)), settings, false);
    public Task ConnectSharedAsync(ProcessStartInfo proxy, AppSettings settings) => ConnectCoreAsync(() => Task.FromResult(new JsonRpcClient(proxy)), settings, true);
    public Task ConnectSharedAsync(Uri endpoint, AppSettings settings) => ConnectCoreAsync(() => JsonRpcClient.ConnectWebSocketAsync(endpoint), settings, true);
    private async Task ConnectCoreAsync(Func<Task<JsonRpcClient>> create, AppSettings settings, bool shared)
    {
        await _gate.WaitAsync();
        try
        {
            Shift.Stop(); Interlocked.Increment(ref _stopVersion); IsBusy = false; _turn = null;
            var connection = Interlocked.Increment(ref _connectionVersion); ClearPending();
            Rates = new(); RatesUpdatedAt = null;
            if (_rpc is not null) await _rpc.DisposeAsync();
            _settings = settings;
            if (shared || IsShared) { ThreadId = null; RestoredHistory = []; _items.Clear(); }
            IsShared = shared; WaitingInOrigin = false; ContinuationReady = false; _quotaTurnId = null;
            _rpc = await create();
            _rpc.MessageReceived += m =>
            {
                var method = Text(m, "method");
                if (m.TryGetProperty("id", out var id) && method is not null)
                {
                    var parameters = m.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
                    var thread = Text(parameters, "threadId");
                    var expected = _loadingThreadId ?? ThreadId;
                    var accept = IsShared ? expected is not null && thread == expected : expected is null || thread is null || thread == expected;
                    if (_forking && thread is not null)
                    {
                        accept = thread != _forkParentThreadId && thread == (Interlocked.CompareExchange(ref _loadingThreadId, thread, null) ?? thread);
                    }
                    if (connection == Volatile.Read(ref _connectionVersion) && accept)
                    {
                        lock (_pendingGate) _pending.Add(new(id.Clone(), method, parameters.Clone()));
                        SetStatus("Precisa de você"); Activity?.Invoke("Solicitação pendente: " + method);
                    }
                    return;
                }
                if (method is "item/commandExecution/outputDelta" or "item/reasoning/textDelta" or "item/reasoning/summaryTextDelta") return;
                _events.Writer.TryWrite((connection, m));
            };
            _rpc.Diagnostic += text => { if (connection == Volatile.Read(ref _connectionVersion)) Activity?.Invoke("Codex: " + text); };
            _rpc.Disconnected += text => { if (connection == Volatile.Read(ref _connectionVersion)) { Shift.Stop(); IsBusy = false; SetStatus("Desconectado"); Warning?.Invoke(text); } };
            SetStatus("Conectando…");
            await _rpc.RequestAsync("initialize", new { clientInfo = new { name = "night_shift", title = "Night Shift", version = "0.6.0" }, capabilities = new { experimentalApi = true } }, TimeSpan.FromSeconds(15));
            await _rpc.NotifyAsync("initialized", new { });
            if (shared) await _rpc.RequestAsync("thread/loaded/list", new { limit = 1 }, TimeSpan.FromSeconds(15));
            if (ThreadId is not null)
            {
                var resumed = await _rpc.RequestAsync("thread/resume", new { threadId = ThreadId, cwd = _project,
                    approvalPolicy = "on-request", approvalsReviewer = _settings.AutoReview ? "auto_review" : "user", sandbox = "workspace-write" });
                RestoredHistory = await LoadHistoryAsync(resumed, ThreadId); _items.Clear(); _firstPrompt = false;
            }
            SetStatus(shared ? "Escolha uma sessão ao vivo" : "Pronto"); Activity?.Invoke(shared ? "Servidor compartilhado conectado. Selecione a sessão usada no outro aplicativo." : "Codex conectado. Configuração local preservada.");
        }
        catch
        {
            if (_rpc is not null) { await _rpc.DisposeAsync(); _rpc = null; }
            SetStatus("Falha ao conectar"); throw;
        }
        finally { _gate.Release(); }
    }

    public Task<JsonElement> AccountAsync() => Rpc.RequestAsync("account/read", new { refreshToken = false });
    public Task<JsonElement> LoginAsync() => Rpc.RequestAsync("account/login/start", new { type = "chatgpt" });
    public Task<JsonElement> SetupSandboxAsync() => Rpc.RequestAsync("windowsSandbox/setupStart", new { mode = "unelevated" });
    public async Task<IReadOnlyList<CodexModel>> ModelsAsync()
    {
        var models = new List<CodexModel>(); string? cursor = null;
        do
        {
            var result = await Rpc.RequestAsync("model/list", new { limit = 50, includeHidden = false, cursor });
            if (result.TryGetProperty("data", out var data)) foreach (var m in data.EnumerateArray())
            {
                var id = Text(m, "model") ?? Text(m, "id"); if (id is null) continue;
                var efforts = m.TryGetProperty("supportedReasoningEfforts", out var opts) ? opts.EnumerateArray()
                    .Select(o => Text(o, "reasoningEffort")).OfType<string>().ToArray() : [];
                models.Add(new(id, Text(m, "displayName") ?? id, Text(m, "defaultReasoningEffort") ?? "medium", efforts));
            }
            cursor = Text(result, "nextCursor");
        } while (cursor is not null && models.Count < 500);
        return models;
    }
    public void UpdateSettings(AppSettings settings) => _settings = settings;
    public void StartShift(int hours)
    {
        if (!IsConnected) throw new InvalidOperationException("Conecte o Codex antes de iniciar o turno.");
        if (IsManualOperation) throw new InvalidOperationException("Revisão e compactação são manuais. Envie uma tarefa antes de ativar o turno.");
        Shift.Arm(DateTimeOffset.UtcNow, hours);
        Activity?.Invoke($"Turno ativado até {Shift.EndsAt:HH:mm}. Retomada automática apenas após limite de uso.");
        Changed?.Invoke();
    }
    public async Task<bool> SendAsync(string project, string text, string model, string effort, IReadOnlyList<Attachment>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(text) && (attachments is null || attachments.Count == 0)) return false;
        AttachmentInput.Validate(attachments);
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException("Selecione uma pasta de projeto válida.");
        var version = Volatile.Read(ref _stopVersion);
        Interlocked.Increment(ref _sending); Changed?.Invoke();
        await _gate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _stopVersion)) return false;
            if (IsBusy && !CanSteer) throw new InvalidOperationException("Este turno ainda não aceita mensagens. Aguarde alguns instantes ou responda à solicitação pendente.");
            if (IsShared && ThreadId is null) throw new InvalidOperationException("Selecione uma sessão ao vivo do servidor compartilhado.");
            if (IsShared && (!CanAcceptSharedInput || WaitingInOrigin || PendingRequests.Count > 0)) throw new InvalidOperationException("Responda às solicitações no aplicativo de origem antes de enviar.");
            var full = Path.GetFullPath(project);
            if (ThreadId is not null && !string.Equals(_project, full, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Inicie uma nova sessão antes de trocar a pasta do projeto.");
            if (IsBusy) return await SteerTurnAsync(text, attachments);
            _settings.Model = model; _settings.Effort = effort; _project = full;
            IsManualOperation = false;
            if (ThreadId is null)
            {
                var start = await Rpc.RequestAsync("thread/start", new
                {
                    cwd = full, model = string.IsNullOrWhiteSpace(model) ? null : model,
                    approvalPolicy = "on-request", approvalsReviewer = _settings.AutoReview ? "auto_review" : "user",
                    sandbox = "workspace-write", serviceName = "night_shift"
                });
                ThreadId = start.GetProperty("thread").GetProperty("id").GetString(); _firstPrompt = true;
            }
            return await StartTurnAsync(text, version, false, attachments);
        }
        finally { Interlocked.Decrement(ref _sending); _gate.Release(); Changed?.Invoke(); }
    }
    private async Task<bool> SteerTurnAsync(string text, IReadOnlyList<Attachment>? attachments)
    {
        var expectedTurnId = _turn ?? throw new InvalidOperationException("O turno ativo ainda não está pronto para receber mensagens.");
        var result = await Rpc.RequestAsync("turn/steer", new { threadId = ThreadId,
            input = AttachmentInput.Build(text, attachments), expectedTurnId });
        if (Text(result, "turnId") != expectedTurnId) throw new IOException("O Codex não confirmou o turno ao receber a mensagem. O rascunho foi preservado.");
        if (!IsShared) ChatChanged?.Invoke(new ChatEntry { Role = "Você", Text = text, Attachments = attachments?.ToList() ?? [] });
        Activity?.Invoke("Mensagem enviada ao turno em andamento.");
        return true;
    }
    private async Task<bool> StartTurnAsync(string text, int version, bool retry, IReadOnlyList<Attachment>? attachments = null)
    {
        if (version != Volatile.Read(ref _stopVersion)) return false;
        AttachmentInput.Validate(attachments);
        Shift.Complete(); ContinuationReady = false; _usageFailure = false; IsBusy = true; SetStatus("Trabalhando");
        var prompt = _firstPrompt ? Protocol.Preamble + "\n\nTarefa do usuário:\n" + text : text;
        var entry = new ChatEntry { Role = retry ? "Night Shift" : "Você", Text = text, Attachments = attachments?.ToList() ?? [] };
        if (!IsShared) ChatChanged?.Invoke(entry);
        try
        {
            var input = AttachmentInput.Build(prompt, attachments);
            object parameters = IsShared ? new { threadId = ThreadId, input } : new
            {
                threadId = ThreadId, input,
                cwd = _project, model = string.IsNullOrWhiteSpace(_settings.Model) ? null : _settings.Model,
                effort = string.IsNullOrWhiteSpace(_settings.Effort) ? null : _settings.Effort,
                approvalPolicy = "on-request", approvalsReviewer = _settings.AutoReview ? "auto_review" : "user",
                sandboxPolicy = new { type = "workspaceWrite", writableRoots = new[] { _project }, networkAccess = false }
            };
            var result = await Rpc.RequestAsync("turn/start", parameters);
            _firstPrompt = false;
            _turn = result.GetProperty("turn").GetProperty("id").GetString(); TurnsStarted++;
            return true;
        }
        catch (RpcException ex) when (IsUsageLimit(ex.DataElement))
        { IsBusy = false; _turn = null; await WaitForQuotaAsync(); return false; }
        catch { IsBusy = false; _turn = null; SetStatus("Precisa de atenção"); throw; }
    }

    public async Task<JsonElement> ResumeAsync(SessionRecord record)
    {
        if (IsShared) return await AttachLiveAsync(record);
        if (!Directory.Exists(record.ProjectPath)) throw new DirectoryNotFoundException("A pasta desta sessão não está disponível.");
        await _gate.WaitAsync();
        try
        {
            if (IsBusy) throw new InvalidOperationException("Pare a sessão atual primeiro.");
            Shift.Stop(); Interlocked.Increment(ref _stopVersion); ClearPending(); _loadingThreadId = record.ThreadId;
            var result = await Rpc.RequestAsync("thread/resume", new { threadId = record.ThreadId, cwd = record.ProjectPath,
                approvalPolicy = "on-request", approvalsReviewer = _settings.AutoReview ? "auto_review" : "user", sandbox = "workspace-write" });
            ThreadId = record.ThreadId; _project = Path.GetFullPath(record.ProjectPath); _firstPrompt = false; _items.Clear(); IsManualOperation = false;
            RestoredHistory = await LoadHistoryAsync(result, record.ThreadId); SetStatus(PendingRequests.Count > 0 ? "Precisa de você" : "Pronto");
            return result;
        }
        finally { _loadingThreadId = null; _gate.Release(); }
    }
    public async Task NewSessionAsync()
    {
        if (IsShared) { await DetachLiveAsync(); return; }
        await StopAsync(); await _gate.WaitAsync();
        try { ThreadId = null; _project = ""; _items.Clear(); RestoredHistory = []; IsManualOperation = false; SetStatus(IsConnected ? "Pronto" : "Desconectado"); }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _stopVersion); Shift.Stop(); ContinuationReady = false; Changed?.Invoke();
        SetStatus("Interrompendo…");
        // A quota/history request holding the state gate must not delay an
        // interrupt for an already known turn. Never abandon a turn/start reply:
        // if its id arrives late, interrupt it below before reporting success.
        var interruptedTurn = _turn;
        var connection = Volatile.Read(ref _connectionVersion);
        if (IsConnected && ThreadId is not null && interruptedTurn is not null)
            await Rpc.RequestAsync("turn/interrupt", new { threadId = ThreadId, turnId = interruptedTurn }, TimeSpan.FromSeconds(15));
        await _gate.WaitAsync();
        try
        {
            if (connection != Volatile.Read(ref _connectionVersion)) return;
            if (interruptedTurn is not null && _turn is not null && _turn != interruptedTurn)
            {
                SetStatus(IsShared ? "Nova tarefa no aplicativo de origem" : "Nova tarefa em andamento");
                Activity?.Invoke("O turno selecionado foi interrompido. Uma tarefa mais recente permanece em andamento.");
                return;
            }
            if (IsConnected && ThreadId is not null && _turn is not null && _turn != interruptedTurn)
                await Rpc.RequestAsync("turn/interrupt", new { threadId = ThreadId, turnId = _turn }, TimeSpan.FromSeconds(15));
            IsBusy = false; _turn = null; WaitingInOrigin = false; ClearPending(); SetStatus(IsConnected ? "Pausado" : "Desconectado");
            Activity?.Invoke("Turno parado. Nenhuma retomada automática pendente.");
        }
        finally { _gate.Release(); }
    }
    public async Task RefreshQuotaAsync()
    {
        var connection = Volatile.Read(ref _connectionVersion);
        var result = await Rpc.RequestAsync("account/rateLimits/read", new { });
        if (connection != Volatile.Read(ref _connectionVersion)) return;
        Rates = RateSnapshot.Parse(result); RatesUpdatedAt = DateTimeOffset.UtcNow; Changed?.Invoke();
    }
    public async Task CheckRetryAsync(DateTimeOffset now, Func<ContinuationDraft>? draftProvider = null)
    {
        if (Shift.Armed && Shift.EndsAt <= now) { await StopAsync(); Activity?.Invoke("O horário final do turno foi alcançado."); return; }
        if (!Shift.CanRetry(now) || IsBusy || PendingRequests.Count != 0 || !IsConnected) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (!Shift.CanRetry(now) || IsBusy || ThreadId is null) return;
            var version = Volatile.Read(ref _stopVersion);
            await RefreshQuotaAsync();
            if (version != Volatile.Read(ref _stopVersion) || !Shift.CanRetry(now) || IsBusy || PendingRequests.Count > 0) return;
            if (IsShared && !await SharedRetryIsCurrentAsync()) return;
            if (version != Volatile.Read(ref _stopVersion) || !Shift.Armed) return;
            if (!Rates.Known || Rates.IsBlocked)
            { Shift.WaitForQuota(Rates.GetRetryAt(now) ?? now.AddMinutes(5)); Changed?.Invoke(); return; }
            if (IsShared)
            {
                // turn/start can steer a newer turn. The protocol has no atomic compare-and-start.
                Shift.Stop(); ContinuationReady = true;
                SetStatus("Limite liberado · confirme a continuação");
                Activity?.Invoke("Limite liberado. A conexão compartilhada exige confirmação para não interferir em outra tarefa.");
                return;
            }
            Activity?.Invoke("Limite disponível. Retomando a mesma conversa.");
            await StartContinuationAsync(version, true, draftProvider);
        }
        catch (Exception ex) { Shift.WaitForQuota(now.AddMinutes(5)); Warning?.Invoke("Não foi possível verificar o limite: " + ex.Message); }
        finally { _gate.Release(); }
    }
    private async Task StartContinuationAsync(int version, bool retry, Func<ContinuationDraft>? draftProvider)
    {
        if (version != Volatile.Read(ref _stopVersion)) return;
        var draft = draftProvider?.Invoke();
        try
        {
            if (await StartTurnAsync(Protocol.ContinueWithDraft(draft?.Text), version, retry, draft?.Attachments)
                && draft is not null && (!string.IsNullOrWhiteSpace(draft.Text) || draft.Attachments?.Count > 0)) draft.Accepted();
        }
        finally { draft?.Finished?.Invoke(); }
    }
    private async Task WaitForQuotaAsync()
    {
        if (IsManualOperation) Shift.Stop();
        try { await RefreshQuotaAsync(); } catch (Exception ex) { Activity?.Invoke("Limite ainda não disponível: " + ex.Message); }
        Shift.WaitForQuota(Rates.GetRetryAt(DateTimeOffset.UtcNow) ?? DateTimeOffset.UtcNow.AddMinutes(5));
        SetStatus(Shift.Armed ? "Aguardando limite" : "Limite atingido");
        Activity?.Invoke("Limite de uso atingido. Próxima verificação: " + Shift.RetryAt?.ToLocalTime().ToString("dd/MM HH:mm"));
    }
    public async Task ResolveRequestAsync(PendingRequest request, object? result)
    {
        lock (_pendingGate) { if (!_pending.Any(p => p.Key == request.Key)) return; }
        if (result is null) await Rpc.ReplyErrorAsync(request.Id, "Este cliente não suporta este pedido. Use uma alternativa compatível.");
        else await Rpc.ReplyAsync(request.Id, result);
        lock (_pendingGate) _pending.RemoveAll(p => p.Key == request.Key);
        SetStatus(PendingRequests.Count > 0 ? "Precisa de você" : IsBusy ? "Trabalhando" : "Pronto");
        Activity?.Invoke("Solicitação respondida: " + request.Title);
    }
    private async Task ProcessEventsAsync()
    {
        try
        {
            await foreach (var message in _events.Reader.ReadAllAsync(_life.Token))
            {
                await _gate.WaitAsync(_life.Token);
                try { if (message.Connection == Volatile.Read(ref _connectionVersion)) await HandleEventAsync(message.Message); }
                catch (Exception ex) { Warning?.Invoke("Evento do Codex: " + ex.Message); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleEventAsync(JsonElement m)
    {
        var method = Text(m, "method"); if (method is null) return;
        var p = m.TryGetProperty("params", out var param) ? param : JsonSerializer.SerializeToElement(new { });
        var thread = Text(p, "threadId");
        if (thread is not null && (IsShared ? ThreadId is null || thread != ThreadId : ThreadId is not null && thread != ThreadId)) return;
        if (m.TryGetProperty("id", out var requestId))
        {
            lock (_pendingGate) _pending.Add(new(requestId.Clone(), method, p.Clone()));
            SetStatus("Precisa de você"); Activity?.Invoke("Solicitação pendente: " + method); return;
        }
        switch (method)
        {
            case "turn/started":
                Shift.Complete(); ContinuationReady = false; _usageFailure = false; WaitingInOrigin = false;
                if (p.TryGetProperty("turn", out var start)) _turn = Text(start, "id");
                IsBusy = true; SetStatus("Trabalhando"); break;
            case "turn/completed":
                IsBusy = false; _turn = null; WaitingInOrigin = false; ClearPending();
                var turn = p.GetProperty("turn");
                if (turn.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object && IsUsageLimit(err) || _usageFailure)
                { _quotaTurnId = Text(turn, "id"); await WaitForQuotaAsync(); }
                else
                {
                    IsManualOperation = false;
                    Shift.Complete(); var status = Text(turn, "status");
                    SetStatus(status == "completed" ? "Concluído" : status == "interrupted" ? "Pausado" : "Precisa de atenção");
                    if (err.ValueKind == JsonValueKind.Object) Warning?.Invoke(Text(err, "message") ?? "Falha durante o turno.");
                }
                break;
            case "thread/status/changed":
                if (IsShared && p.TryGetProperty("status", out var runtime)) ApplySharedStatus(runtime);
                break;
            case "thread/closed":
                if (IsShared) { Shift.Stop(); IsBusy = false; WaitingInOrigin = false; CanAcceptSharedInput = false; SetStatus("Sessão encerrada no servidor"); }
                break;
            case "error":
                if (p.TryGetProperty("error", out var error))
                { _usageFailure = IsUsageLimit(error); Activity?.Invoke(Text(error, "message") ?? "Erro reportado pelo Codex."); }
                break;
            case "account/rateLimits/updated": Rates = RateSnapshot.Parse(p); RatesUpdatedAt = DateTimeOffset.UtcNow; Changed?.Invoke(); break;
            case "item/agentMessage/delta": UpdateChat(Text(p, "itemId") ?? "", Text(p, "delta") ?? "", true); break;
            case "item/completed":
            case "item/started":
                if (!p.TryGetProperty("item", out var item)) break;
                var type = Text(item, "type"); var id = Text(item, "id") ?? Guid.NewGuid().ToString("N");
                if (type == "agentMessage") UpdateChat(id, Text(item, "text") ?? "", false);
                else if (IsShared && type == "userMessage")
                {
                    var entries = ThreadHistory.ReadTurns(JsonSerializer.SerializeToElement(new[] { new { items = new[] { item } } }));
                    foreach (var entry in entries) ChatChanged?.Invoke(entry);
                }
                else if (type == "commandExecution") Activity?.Invoke((method == "item/started" ? "Executando: " : "Comando " + Text(item, "status") + ": ") + Text(item, "command"));
                else if (type == "fileChange") Activity?.Invoke("Arquivos: " + Text(item, "status"));
                else if (type == "mcpToolCall") Activity?.Invoke("Integração: " + Text(item, "server") + " / " + Text(item, "tool") + " · " + Text(item, "status"));
                else if (type == "contextCompaction") Activity?.Invoke("Codex compactou o contexto da conversa.");
                break;
            case "item/autoApprovalReview/started": Activity?.Invoke("Revisor avaliando uma ação…"); break;
            case "item/autoApprovalReview/completed":
                if (p.TryGetProperty("review", out var review)) Activity?.Invoke("Revisão: " + Text(review, "status") + " · " + Text(review, "rationale"));
                break;
            case "serverRequest/resolved":
                if (p.TryGetProperty("requestId", out var resolved)) { lock (_pendingGate) _pending.RemoveAll(r => r.Key == resolved.GetRawText()); Changed?.Invoke(); } break;
            case "account/login/completed":
                if (p.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) Activity?.Invoke("Login concluído.");
                else Warning?.Invoke(Text(p, "error") ?? "Login não concluído."); Changed?.Invoke(); break;
            case "account/updated": Changed?.Invoke(); break;
            case "windowsSandbox/setupCompleted": Activity?.Invoke("Preparação do sandbox: " + p.GetRawText()); break;
        }
    }
    private void UpdateChat(string id, string text, bool append)
    {
        if (!_items.TryGetValue(id, out var entry)) { entry = new ChatEntry { Id = id }; _items[id] = entry; }
        entry.Text = append ? entry.Text + text : text;
        ChatChanged?.Invoke(entry);
        if (_items.Count > 450) _items.Remove(_items.Keys.First());
    }
    private static bool IsUsageLimit(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return false;
        if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) error = data;
        if (error.TryGetProperty("codexErrorInfo", out var info))
        {
            if (info.ValueKind == JsonValueKind.String) return string.Equals(info.GetString(), "usageLimitExceeded", StringComparison.OrdinalIgnoreCase);
            if (info.ValueKind == JsonValueKind.Object) return info.EnumerateObject().Any(v => v.Name.Equals("usageLimitExceeded", StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }
    public static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private JsonRpcClient Rpc => _rpc is { IsAlive: true } ? _rpc : throw new IOException("Conecte o Codex primeiro.");
    private void SetStatus(string status) { Status = status; Changed?.Invoke(); }
    private void ClearPending() { lock (_pendingGate) _pending.Clear(); Changed?.Invoke(); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Shift.Stop(); Interlocked.Increment(ref _stopVersion); _life.Cancel(); _events.Writer.TryComplete();
        if (_rpc is not null) await _rpc.DisposeAsync();
        try { await _eventLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _life.Dispose();
    }
}
