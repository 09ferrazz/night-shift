using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using NightShift.Core;

namespace NightShift.App;

public sealed class DisplayMessage : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Role { get; init; } = "Codex";
    public string RoleInitial => Role == "Você" ? "V" : Role is "Codex" or "Claude" ? "☾" : "!";
    public DateTimeOffset Time { get; init; }
    public string TimeLabel => Time == DateTimeOffset.MinValue ? "—" : Time.ToLocalTime().ToString("HH:mm");
    public string Background => Role == "Você" ? "#20283F" : "#10182A";
    public string Accent => Role == "Você" ? "#A996FF" : Role == "Codex" ? "#83E6C3" : "#FFD78C";
    private List<Attachment> _attachments = [];
    public List<Attachment> Attachments { get => _attachments; set { _attachments = value; foreach(var name in new[] { nameof(Attachments), nameof(AttachmentSummary), nameof(HasAttachments), nameof(CopyText) }) PropertyChanged?.Invoke(this,new(name)); } }
    public string AttachmentSummary => string.Join("\n", Attachments.Select(a => "📎 " + a.Name));
    public bool HasAttachments => Attachments.Count > 0;
    public string CopyText => Text + (HasAttachments ? "\n\n" + string.Join("\n", Attachments.Select(a => a.Name + ": " + a.Path)) : "");
    private string _text = "";
    public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); PropertyChanged?.Invoke(this, new(nameof(CopyText))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ChatEntry ToEntry() => new() { Id = Id, Role = Role, Text = Text, Time = Time, Attachments = Attachments.ToList() };
}

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AppStore _store;
    private IAgentSession _session;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _clock;
    private readonly ConcurrentDictionary<string, ChatEntry> _inbox = new();
    private readonly ConcurrentQueue<string> _activity = new();
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly Dictionary<string, DisplayMessage> _visible = new();
    private readonly List<DisplayMessage> _transcript = [];
    private readonly AppSettings _settings;
    private bool _dirty;
    private bool _busy;
    private bool _stopping;
    private bool _ticking;
    private bool _sleepHeld;
    private bool _disposed;
    private int _clockCount;
    private string _draft = "";
    private long _draftRevision;
    private string _notice = "";
    private string _accountLabel = "Sua conta Codex";
    private string _sessionTitle = "Nova sessão";
    private string? _displayedThreadId;
    private CodexModel? _model;
    private string _effort = "";
    private readonly List<string> _reportActivity = [];
    public ObservableCollection<DisplayMessage> Messages { get; } = [];
    public ObservableCollection<string> Activities { get; } = [];
    public ObservableCollection<SessionRecord> Sessions { get; } = [];
    public ObservableCollection<SessionRecord> RecentSessions { get; } = [];
    public ObservableCollection<CodexModel> Models { get; } = [];
    public ObservableCollection<string> Efforts { get; } = [];
    public int[] HourOptions { get; } = Enumerable.Range(1, 24).ToArray();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ChatUpdated;
    public event Action? ChatReset;
    public bool HasEarlierMessages => Messages.Count < _transcript.Count;
    public string EarlierMessagesLabel => $"Mostrar mensagens anteriores ({_transcript.Count - Messages.Count})";

    public MainViewModel(AppStore? store = null, IAgentSession? session = null, Func<AgentProvider, IAgentSession>? sessionFactory = null)
    {
        _store = store ?? new();
        _sessionFactory = sessionFactory ?? (provider => provider == AgentProvider.Claude ? new ClaudeSession(_store) : new CodexSession());
        if (store is null) PortableDataMigration.TryImportPreviousVersion(_store);
        Commands = CodexCommands.All.Select(c => new CommandAction(c, () => CanExecuteCommand(c.Name), () => SupportsCommand(c.Name), () => ProviderLabel)).ToArray();
        _settings = _store.LoadSettings(); _session = session ?? _sessionFactory(Provider); _effort = ActiveEffort; _accountLabel = "Sua conta " + ProviderLabel;
        LoadProviderCache(); BindSession();
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => Flush(); _renderTimer.Start();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += async (_, _) => await TickAsync(); _clock.Start();
        _activity.Enqueue("Escolha a pasta do projeto e conecte sua IA.");
    }
    public string ProjectPath { get => _settings.ProjectPath; set { _settings.ProjectPath = value; NotifyAll(); } }
    public string CodexPath { get => _settings.CodexPath; set { _settings.CodexPath = value; NotifyAll(); } }
    public string ProjectName => string.IsNullOrWhiteSpace(ProjectPath) ? "Nenhum projeto selecionado" : new DirectoryInfo(ProjectPath.TrimEnd(Path.DirectorySeparatorChar)).Name;
    public bool AutoReview { get => _settings.AutoReview; set { _settings.AutoReview = value; NotifyAll(); } }
    public bool PreventSleep { get => _settings.PreventSleep; set { _settings.PreventSleep = value; NotifyAll(); UpdateSleep(); } }
    public int ShiftHours { get => _settings.ShiftHours; set { _settings.ShiftHours = Math.Clamp(value, 1, 24); NotifyAll(); } }
    public string Draft { get => _draft; set { if (_draft != value) { _draft = value; _draftRevision++; } NotifyAll(); } }
    public string Notice { get => _notice; set { _notice = value; Notify(nameof(Notice)); Notify(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public string AccountLabel => _accountLabel;
    public string ConnectionLabel => CompatibilityLabel;
    public string ConnectLabel => _session.IsConnected ? "Desconectar" : "Conectar";
    public bool CanConnect => !_busy && !_switchingProvider && (IsConnected && _session.IsShared || !_session.IsBusy && !_session.Shift.Armed);
    public bool IsConnected => _session.IsConnected;
    public bool CanEdit => !_busy && !_switchingProvider && !_stopping && !_session.IsBusy;
    public bool CanCompose => !_busy && !_switchingProvider;
    public bool CanSend { get { var command = CodexCommands.Parse(Draft); return !_addingAttachments && HasDraft && (command.IsCommand ? command.Error is not null || !SupportsCommand(command.Definition!.Name) ? CanEdit : CanExecuteCommand(command.Definition!.Name, command.Argument) : !_busy && !_stopping && (CanEdit || _session.CanSteer) && IsConnected && Directory.Exists(ProjectPath) && (!IsSharedPlatform || HasLiveSession && _session.CanAcceptSharedInput && !_session.WaitingInOrigin && !HasPending)); } }
    public bool CanChangeProject => !_busy && !_switchingProvider && _session.ThreadId is null && !_session.IsBusy;
    public bool CanStop => !_session.IsReadOnly && !_switchingProvider && !_stopping && !_disposed && (_session.IsBusy || _session.IsPreparing || _session.ContinuationReady || _session.Shift.Armed || _session.Shift.WaitingForQuota || _session.PendingRequests.Count > 0);
    public bool CanStartShift => !_busy && !_switchingProvider && !_stopping && IsConnected && !_session.Shift.Armed && !_session.IsManualOperation && !CodexCommands.Parse(Draft).IsCommand && (!IsSharedPlatform || HasLiveSession && _session.CanAcceptSharedInput) && (_session.IsBusy || _session.Shift.WaitingForQuota || HasDraft && Directory.Exists(ProjectPath));
    public string NightButtonLabel => _session.Shift.Armed ? "Turno ativo" : "☾  Iniciar turno";
    public bool IsChatEmpty => Messages.Count == 0;
    public bool IsHistoryEmpty => Sessions.Count == 0;
    public string Status => _stopping ? $"Interrompendo… aguardando o {ProviderLabel}" : _session.Status;
    public string StatusAccent => _session.PendingRequests.Count > 0 || _session.Shift.WaitingForQuota ? "#FFD78C" : _session.IsConnected ? "#83E6C3" : "#73809F";
    public string ShiftLabel => _session.Shift.Armed ? "Até " + _session.Shift.EndsAt?.ToLocalTime().ToString("HH:mm") : "Desativado";
    public string RetryLabel
    {
        get
        {
            if (!_session.Shift.WaitingForQuota) return "—";
            if (!_session.Shift.Armed) return "Ative o turno";
            var remaining = _session.Shift.RetryAt - DateTimeOffset.UtcNow;
            if (remaining is null || remaining <= TimeSpan.Zero) return "Verificando…";
            if (remaining > TimeSpan.FromHours(24)) return _session.Shift.RetryAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—";
            return $"Em {(int)remaining.Value.TotalHours:00}:{remaining.Value.Minutes:00}:{remaining.Value.Seconds:00}";
        }
    }
    public double UsedPercent => _session.Rates.Primary?.UsedPercent ?? 0;
    public string BudgetLabel => _session.Rates.Primary is { } w ? $"Janela principal: {w.UsedPercent:0}% usado" : "Limites não informados pelo " + ProviderLabel;
    public string SecondaryBudgetLabel => _session.Rates.Secondary is { } w ? $"Janela secundária: {w.UsedPercent:0}% usado" : "";
    public string ReviewLabel => IsSharedPlatform ? "Permissões definidas pelo aplicativo de origem" : AutoReview ? IsClaudeProvider ? "Revisão nativa solicitada quando disponível" : "Revisão automática ativada" : "Aprovações manuais";
    public string SleepLabel => _sleepHeld ? "PC acordado até o fim do turno" : "Suspensão conforme o Windows";
    public bool HasPending => _session.PendingRequests.Count > 0;
    public string PendingLabel => _session.PendingRequests.FirstOrDefault()?.Title ?? "";
    public string ComposerHint => _session.IsReadOnly ? ReadOnlyHint : _session.IsBusy ? _session.CanSteer ? $"Você pode enviar uma orientação enquanto o {ProviderLabel} trabalha." : "Aguarde o turno ficar pronto ou responda à solicitação pendente." : "Descreva uma tarefa ou use /help para ver os comandos.";
    public IReadOnlyList<PendingRequest> PendingRequests => _session.PendingRequests;
    public CodexModel? SelectedModel
    {
        get => _model;
        set
        {
            if (_switchingProvider && value is null) return;
            var wantedEffort = ActiveEffort;
            _model = value; ActiveModel = value?.Id ?? ""; Efforts.Clear();
            if (value is not null) foreach (var effort in value.Efforts) Efforts.Add(effort);
            SelectedEffort = value is not null && value.Efforts.Contains(wantedEffort) ? wantedEffort : value?.DefaultEffort ?? "";
            NotifyAll();
        }
    }
    public string SelectedEffort { get => _effort; set { if (_switchingProvider && value is null) return; _effort = value; ActiveEffort = value; Notify(nameof(SelectedEffort)); } }

    public async Task RunAsync(Func<Task> action)
    {
        if (_busy || _switchingProvider) return;
        _busy = true; Notice = ""; NotifyAll();
        try { await action(); }
        catch (Exception ex) { Notice = ex.Message; _activity.Enqueue("Atenção: " + ex.Message); }
        finally { _busy = false; Flush(); NotifyAll(); }
    }
    public async Task ConnectAsync()
    {
        if (IsSharedPlatform)
        {
            if (IsClaudeProvider) { await ConnectClaudeAsync(true); return; }
            Save();
            try
            {
                var endpoint = SharedEndpoint.Trim();
                if (endpoint.StartsWith("ws", StringComparison.OrdinalIgnoreCase)) await _session.ConnectSharedAsync(SharedConnection.ParseWebSocket(endpoint), _settings);
                else
                {
                    var binary = CodexLocator.Find(CodexPath) ?? throw new FileNotFoundException("Selecione o Codex CLI em Ajustes para usar o proxy oficial, ou informe um WebSocket local.");
                    var socket = endpoint.Length == 0 ? SharedConnection.DefaultSocket : endpoint;
                    await _session.ConnectSharedAsync(CodexLocator.ProxyInfo(binary, socket), _settings);
                }
                _displayedThreadId = null; ReplaceMessages([]);
                try { await RefreshAccountAsync(); await _session.RefreshQuotaAsync(); } catch (Exception ex) { _activity.Enqueue("Conta/limites: " + ex.Message); }
            }
            catch (Exception ex)
            {
                await _session.DisconnectAsync();
                throw new IOException("Conexão compartilhada indisponível. Confira se o aplicativo usa um app-server acessível e revise Opções de conexão. Nenhuma sessão CLI foi iniciada. Detalhe: " + ex.Message, ex);
            }
            try { await RefreshLiveAsync(); }
            catch (Exception ex) { Notice = (IsConnected ? "Servidor conectado; não foi possível atualizar a lista de sessões: " : "Conexão perdida ao atualizar as sessões: ") + ex.Message; _activity.Enqueue(Notice); }
            PageRequested?.Invoke("Live"); Save();
            return;
        }
        if (IsClaudeProvider) { await ConnectClaudeAsync(false); return; }
        var path = CodexLocator.Find(CodexPath);
        if (path is null) throw new FileNotFoundException("Codex não encontrado. Em Ajustes, selecione o executável do Codex ou abra a ajuda de instalação.");
        await _session.ConnectAsync(CodexLocator.StartInfo(path, AutoReview), _settings);
        if (_session.RestoredHistory.Count > 0) ReplaceMessages(_session.RestoredHistory);
        _settings.CodexPath = path;
        var wanted = ActiveModel; var wantedEffort = ActiveEffort;
        var available = await _session.ModelsAsync(); Models.Clear();
        Models.Add(new("", "Configuração local do Codex", "", []));
        foreach (var model in available) Models.Add(model);
        SelectedModel = Models.FirstOrDefault(m => m.Id == wanted) ?? Models.FirstOrDefault();
        if (SelectedModel?.Efforts.Contains(wantedEffort) == true) SelectedEffort = wantedEffort;
        await RefreshAccountAsync();
        try { await _session.RefreshQuotaAsync(); } catch (Exception ex) { _activity.Enqueue("Limites: " + ex.Message); }
        try { await RefreshRecentAsync(); } catch (Exception ex) { _activity.Enqueue("Conversas recentes: " + ex.Message); }
        Save();
    }
    public async Task RefreshAccountAsync()
    {
        var result = await _session.AccountAsync();
        if (result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
        {
            var kind = CodexSession.Text(account, "type");
            _accountLabel = CodexSession.Text(account, "email") ?? (kind == "apiKey" ? "Conta com chave API" : "Conta " + ProviderLabel + " conectada");
        }
        else _accountLabel = "Login necessário";
        NotifyAll();
    }
    public async Task<JsonElement> LoginAsync() => await _session.LoginAsync();
    public async Task SetupSandboxAsync() { await _session.SetupSandboxAsync(); _activity.Enqueue("Preparando sandbox. Aguarde o resultado na atividade."); }
    public async Task SendAsync()
    {
        if (!HasDraft || _addingAttachments) return;
        var text = Draft;
        var revision = _draftRevision; var attached = Attachments.ToArray();
        var command = CodexCommands.Parse(text);
        if (command.IsCommand && attached.Length > 0) throw new InvalidOperationException("Envie anexos com uma mensagem, sem comando iniciado por /. Os anexos foram mantidos.");
        if (command.IsCommand) { await ExecuteCommandAsync(command); Draft = ""; Save(); return; }
        if (_session.ThreadId is null) _sessionTitle = text.Length > 80 ? text[..80] + "…" : text;
        _session.UpdateSettings(_settings);
        PinAttachments(attached);
        var accepted = false;
        try
        {
            accepted = await _session.SendAsync(ProjectPath, text, SelectedModel?.Id ?? "", SelectedEffort, attached);
            _displayedThreadId = _session.ThreadId;
            if (accepted) AcceptDraft(revision, attached);
            _dirty = true; Save();
        }
        finally { ReleaseAttachments(attached, accepted); }
    }
    public async Task StartShiftAsync()
    {
        if (CodexCommands.Parse(Draft).IsCommand) throw new InvalidOperationException("Use Enviar para executar comandos. Iniciar turno precisa de uma tarefa.");
        if (!HasDraft && !_session.IsBusy && !_session.Shift.WaitingForQuota)
            throw new InvalidOperationException($"Escreva uma tarefa ou inicie o turno enquanto o {ProviderLabel} estiver trabalhando.");
        _session.StartShift(ShiftHours); UpdateSleep();
        _nightShiftRequested = false;
        try { if (HasDraft && !_session.IsBusy) await SendAsync(); }
        catch { await _session.StopAsync(); UpdateSleep(); throw; }
    }
    public async Task StopAsync()
    {
        if (_stopping) return;
        _nightShiftRequested = false;
        _stopping = true; Notice = ""; NotifyAll();
        try { await _session.StopAsync(); Save(); }
        catch (Exception ex) { Notice = "Não foi possível confirmar a interrupção: " + ex.Message; _activity.Enqueue(Notice); }
        finally { _stopping = false; UpdateSleep(); NotifyAll(); }
    }
    public async Task NewSessionAsync()
    {
        _nightShiftRequested = false;
        Save(); await _session.NewSessionAsync(); ClearAttachmentDraft(); Flush(); ReplaceMessages([]); _sessionTitle = "Nova sessão"; _displayedThreadId = null; _sessionSource = "night_shift"; Draft = ""; NotifyAll(); UpdateSleep();
    }
    public async Task ResumeAsync(SessionRecord record)
    {
        if (record.Provider != Provider) throw new InvalidOperationException("Escolha a IA correspondente antes de retomar esta conversa.");
        Save();
        try { await _session.ResumeAsync(record); }
        catch
        {
            if (_session.IsShared && _session.ThreadId is null) { _displayedThreadId = null; ReplaceMessages([]); _sessionTitle = "Escolha uma sessão ao vivo"; NotifyAll(); }
            throw;
        }
        _settings.ProjectPath = record.ProjectPath; _sessionTitle = record.Title; _sessionSource = record.Source;
        ClearAttachmentDraft();
        var history = _session.RestoredHistory;
        ReplaceMessages(history.Count > 0 ? history : _store.LoadSession(record.ThreadId, Provider)?.Messages ?? []);
        _displayedThreadId = _session.ThreadId;
        Flush(); Save(); NotifyAll(); UpdateSleep();
    }
    public Task ResolveAsync(PendingRequest request, object? answer) => _session.ResolveRequestAsync(request, answer);
    private void Flush()
    {
        var updates = new Dictionary<string, ChatEntry>();
        foreach (var key in _inbox.Keys) if (_inbox.TryRemove(key, out var e)) updates[key] = e;
        foreach (var entry in updates.Values.OrderBy(e => e.Time))
        {
            if (!_visible.TryGetValue(entry.Id, out var display))
            {
                display = new DisplayMessage { Id = entry.Id, Role = entry.Role, Time = entry.Time, Attachments = entry.Attachments.ToList() };
                _visible[entry.Id] = display; _transcript.Add(display); Messages.Add(display);
            }
            display.Text = entry.Text;
            if (!display.Attachments.SequenceEqual(entry.Attachments)) display.Attachments = entry.Attachments.ToList();
            _dirty = true;
        }
        for (int i = 0; i < 50 && _activity.TryDequeue(out var text); i++)
        {
            var item = DateTime.Now.ToString("HH:mm") + "  " + (text.Length > 1500 ? text[..1500] + "…" : text);
            Activities.Insert(0, item); _reportActivity.Add(item);
            if (Activities.Count > 150) Activities.RemoveAt(Activities.Count - 1);
            if (_reportActivity.Count > 500) _reportActivity.RemoveAt(0);
        }
        if (_warnings.TryDequeue(out var warning)) Notice = warning;
        if (updates.Count > 0) ChatUpdated?.Invoke();
        NotifyAll();
    }
    private void ReplaceMessages(IEnumerable<ChatEntry> entries)
    {
        _inbox.Clear(); Messages.Clear(); _visible.Clear(); _transcript.Clear();
        foreach (var entry in entries)
        {
            var display = new DisplayMessage { Id = entry.Id, Role = entry.Role, Time = entry.Time, Text = entry.Text, Attachments = entry.Attachments.ToList() };
            _visible[entry.Id] = display; _transcript.Add(display);
        }
        foreach (var display in _transcript.TakeLast(400)) Messages.Add(display);
        ChatReset?.Invoke(); NotifyAll();
    }
    public void LoadEarlierMessages()
    {
        var remaining = _transcript.Count - Messages.Count;
        foreach (var display in _transcript.Skip(Math.Max(0, remaining - 200)).Take(Math.Min(remaining, 200)).Reverse())
            Messages.Insert(0, display);
        NotifyAll();
    }
    private async Task TickAsync()
    {
        if (_ticking || _disposed || _switchingProvider || _addingAttachments) return;
        _ticking = true;
        try
        {
            await _session.CheckRetryAsync(DateTimeOffset.UtcNow, CaptureContinuationDraft); UpdateSleep(); NotifyAll();
            if (++_clockCount % 3 == 0 && _dirty) Save();
        }
        catch (Exception ex) { Notice = ex.Message; }
        finally { _ticking = false; NotifyAll(); }
    }
    public void Save()
    {
        _session.UpdateSettings(_settings); _store.SaveSettings(_settings);
        if (_displayedThreadId is { } id && id == _session.ThreadId)
        {
            Flush();
            var record = new SessionRecord { Provider = Provider, ThreadId = id, ProjectPath = ProjectPath, Title = _sessionTitle,
                Source = _sessionSource, Messages = _transcript.Select(m => m.ToEntry()).ToList(), UpdatedAt = DateTimeOffset.Now };
            _store.SaveSession(record); var old = Sessions.FirstOrDefault(r => r.ThreadId == id);
            if (old is not null) Sessions.Remove(old);
            var summary = new SessionRecord { Provider = Provider, ThreadId = id, ProjectPath = ProjectPath, Title = _sessionTitle, Source = _sessionSource, UpdatedAt = record.UpdatedAt };
            Sessions.Insert(0, summary);
            if (Sessions.Count > 100) Sessions.RemoveAt(Sessions.Count - 1);
            UpsertRecent(summary);
        }
        _dirty = false; NotifyAll();
    }
    public string Report()
    {
        Flush(); var sb = new StringBuilder(); sb.AppendLine("# Night Shift — relatório da sessão\n");
        sb.AppendLine($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}\n\nProjeto: {ProjectPath}\n\nEstado: {_session.Status}\n\nModelo: {SelectedModel?.Name ?? "configuração local"}\n");
        sb.AppendLine("## Conversa\n\nInclui todas as mensagens recebidas ou carregadas nesta sessão, inclusive as anteriores recolhidas na interface. Conteúdo não retornado pela IA não está incluído.\n");
        foreach (var m in _transcript) sb.AppendLine($"### {m.Role} · {m.TimeLabel}\n\n{m.CopyText}\n");
        sb.AppendLine("## Atividade\n"); foreach (var line in _reportActivity) sb.AppendLine("- " + line);
        return sb.ToString();
    }
    private void UpdateSleep()
    {
        var hold = _session.Shift.Armed && PreventSleep;
        if (hold == _sleepHeld) return;
        NativeMethods.SetAwake(hold); _sleepHeld = hold; Notify(nameof(SleepLabel));
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyAll() { PropertyChanged?.Invoke(this, new(null)); foreach (var command in Commands) command.Refresh(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _renderTimer.Stop(); _clock.Stop();
        await _providerGate.WaitAsync();
        try
        {
            try { Save(); } finally { ClearAttachmentDraft(); foreach (var state in _providerDrafts.Where(p => p.Key != Provider).Select(p => p.Value)) foreach (var item in state.Attachments) DiscardUnused(item); UnbindSession(); NativeMethods.SetAwake(false); await _session.DisposeAsync(); }
        }
        finally { _providerGate.Release(); }
    }
}
