using System.IO;
using NightShift.Core;

namespace NightShift.App;

public sealed partial class MainViewModel
{
    private readonly Func<AgentProvider, IAgentSession> _sessionFactory;
    private readonly Dictionary<AgentProvider, (string Draft, Attachment[] Attachments)> _providerDrafts = [];
    private bool _switchingProvider;
    private readonly SemaphoreSlim _providerGate = new(1, 1);
    private Action<ChatEntry>? _chatHandler;
    private Action<string>? _activityHandler;
    private Action<string>? _warningHandler;
    public AgentProvider Provider => _settings.Provider;
    public string ProviderLabel => Provider == AgentProvider.Claude ? "Claude" : "Codex";
    public bool IsCodexProvider => Provider == AgentProvider.Codex;
    public bool IsClaudeProvider => Provider == AgentProvider.Claude;
    public string ClaudePath { get => _settings.ClaudePath; set { _settings.ClaudePath = value; NotifyAll(); } }
    public string AgentPath { get => IsClaudeProvider ? ClaudePath : CodexPath; set { if (IsClaudeProvider) ClaudePath = value; else CodexPath = value; } }
    public string LocalAgentLabel => ProviderLabel + " local";
    public string LocalAgentHint => $"Deixe vazio para localizar o {ProviderLabel} instalado no seu computador.";
    public string EmptyConversationHint => $"Escolha um projeto e dê o próximo passo com o {ProviderLabel}.";
    public string ComposerAccessibleName => "Mensagem para o " + ProviderLabel;
    public string HistoryDescription => $"Histórico do {ProviderLabel} local, inclusive as conversas iniciadas no terminal.";
    public string ModelsDescription => IsClaudeProvider ? "Aliases nativos do Claude Code; disponibilidade depende da sua conta e instalação." : "As opções disponíveis são consultadas na sua instalação do Codex.";
    public string LoginLabel => "Entrar no " + ProviderLabel;
    public string ReviewOptionLabel => IsClaudeProvider ? "Usar revisão nativa do Claude quando disponível" : "Usar o revisor automático do Codex";
    public string ReviewOptionHint => IsClaudeProvider ? "Usa somente o modo nativo de revisão quando disponível. Nenhuma permissão é ignorada; decisões necessárias ficam pendentes." : "Um revisor separado avalia as ações que pedem permissão. Pedidos que exigirem sua decisão ficam pendentes.";
    public bool CanSwitchProvider => !_disposed && !_switchingProvider && !_busy && !_stopping && !_ticking && !_addingAttachments && !_session.IsBusy && !_session.IsPreparing && !_session.IsManualOperation && !_session.Shift.Armed && !_session.Shift.WaitingForQuota && !_session.ContinuationReady && !HasPending;
    public string ProviderSwitchHint => CanSwitchProvider ? "Escolher a IA para esta conversa" : IsReadOnlySession ? "Desconecte a observação para trocar a IA. O processo no terminal de origem continua ativo." : "Aguarde o trabalho terminar e resolva pedidos ou pare o turno antes de trocar a IA.";
    public bool CanEditLocalSettings => IsCliPlatform && CanEdit && !_session.IsPreparing;
    public string CommandCapabilityHint => IsClaudeProvider ? "Claude oferece new, resume, model, effort, status, login e stop. Comandos específicos do Codex ficam indisponíveis." : "Revisar e compactar desativam o turno automático. /fork cria uma conversa nova; os arquivos do projeto continuam compartilhados.";
    public bool CanSetupSandbox => IsCodexProvider && CanUseCliSettings;
    public bool CanOpenOrigin => !_busy && _session.CanOpenOrigin;
    public bool IsReadOnlySession => _session.IsReadOnly;
    public string ReadOnlyHint => _session.IsReadOnly ? "Sessão externa em observação. Envie tarefas e responda permissões no terminal de origem." : "";
    public Task OpenOriginAsync() => _session.OpenOriginAsync();
    private string ActiveModel { get => IsClaudeProvider ? _settings.ClaudeModel : _settings.Model; set { if (IsClaudeProvider) _settings.ClaudeModel = value; else _settings.Model = value; } }
    private string ActiveEffort { get => IsClaudeProvider ? _settings.ClaudeEffort : _settings.Effort; set { if (IsClaudeProvider) _settings.ClaudeEffort = value; else _settings.Effort = value; } }
    private void BindSession()
    {
        var source = _session;
        _chatHandler = e => { if (ReferenceEquals(source, _session)) _inbox[e.Id] = new ChatEntry { Id = e.Id, Role = e.Role, Kind = e.Kind, Time = e.Time, Text = e.Text, Attachments = e.Attachments.ToList() }; };
        _activityHandler = text => { if (!ReferenceEquals(source, _session)) return; _activity.Enqueue(text); while (_activity.Count > 300) _activity.TryDequeue(out _); };
        _warningHandler = text => { if (ReferenceEquals(source, _session)) _warnings.Enqueue(text); };
        source.ChatChanged += _chatHandler; source.Activity += _activityHandler; source.Warning += _warningHandler;
    }
    private void UnbindSession()
    {
        _session.ChatChanged -= _chatHandler; _session.Activity -= _activityHandler; _session.Warning -= _warningHandler;
    }
    private void LoadProviderCache()
    {
        Sessions.Clear(); RecentSessions.Clear();
        foreach (var record in _store.LoadSessions().Where(r => r.Provider == Provider)) { Sessions.Add(record); RecentSessions.Add(record); }
        _historyCursor = null; _historyCursors.Clear(); _loadedProject = null; _loadedSearch = "";
        _historyLabel = $"Atualize para consultar também as conversas originais do {ProviderLabel}.";
    }
    public async Task SwitchProviderAsync(AgentProvider provider)
    {
        if (!Enum.IsDefined(provider) || provider == Provider) return;
        if (!CanSwitchProvider) { Notice = ProviderSwitchHint; return; }
        _switchingProvider = true; NotifyAll();
        await _providerGate.WaitAsync();
        IAgentSession? incoming = null; var swapped = false;
        try
        {
            Save();
            var reconnect = IsConnected;
            incoming = _sessionFactory(provider);
            _providerDrafts[Provider] = (Draft, Attachments.ToArray());
            await _session.DisconnectAsync(); await _session.DisposeAsync(); UnbindSession();
            _session = incoming; swapped = true; _settings.Provider = provider; BindSession();
            _inbox.Clear(); while (_warnings.TryDequeue(out _)) { }
            _attachmentContext++; Attachments.Clear();
            var state = _providerDrafts.GetValueOrDefault(provider, (Draft: "", Attachments: Array.Empty<Attachment>()));
            Draft = state.Draft; foreach (var item in state.Attachments) Attachments.Add(item);
            _model = null; _effort = ActiveEffort; Models.Clear(); Efforts.Clear();
            LiveSessions.Clear(); ReplaceMessages([]); _displayedThreadId = null; _sessionTitle = "Nova sessão"; _sessionSource = "night_shift";
            _nightShiftRequested = false; _limitsError = ""; _accountLabel = "Sua conta " + ProviderLabel;
            LoadProviderCache(); Save(); UpdateSleep();
            if (reconnect && !_disposed)
            {
                var sharedWebSocket = IsCodexProvider && IsSharedPlatform && SharedEndpoint.Trim().StartsWith("ws", StringComparison.OrdinalIgnoreCase);
                var available = sharedWebSocket || (IsClaudeProvider ? ClaudeLocator.Find(ClaudePath) is not null : CodexLocator.Find(CodexPath) is not null);
                if (available) await ConnectAsync();
                else Notice = $"{ProviderLabel} selecionado. Localize sua instalação em Ajustes para conectar.";
            }
        }
        catch (Exception ex) { if (!swapped && incoming is not null) await incoming.DisposeAsync(); Notice = ex.Message; _activity.Enqueue("Atenção: " + ex.Message); }
        finally { _switchingProvider = false; _providerGate.Release(); NotifyAll(); }
    }
    private async Task ConnectClaudeAsync(bool shared)
    {
        var path = ClaudeLocator.Find(ClaudePath) ?? throw new FileNotFoundException("Claude Code não encontrado. Em Ajustes, selecione a instalação local ou abra a ajuda de instalação.");
        Save();
        if (shared) await _session.ConnectSharedAsync(ClaudeLocator.StartInfo(path), _settings);
        else await _session.ConnectAsync(ClaudeLocator.StartInfo(path), _settings);
        _settings.ClaudePath = path;
        _displayedThreadId = _session.ThreadId; ReplaceMessages(_session.RestoredHistory);
        if (!shared)
        {
            var wanted = ActiveModel; var effort = ActiveEffort;
            var available = await _session.ModelsAsync(); Models.Clear();
            Models.Add(new("", "Configuração local do Claude", "", []));
            foreach (var model in available) Models.Add(model);
            SelectedModel = Models.FirstOrDefault(m => m.Id == wanted) ?? Models.FirstOrDefault();
            if (SelectedModel?.Efforts.Contains(effort) == true) SelectedEffort = effort;
            await RefreshRecentAsync();
        }
        else { await RefreshLiveAsync(); PageRequested?.Invoke("Live"); }
        await RefreshAccountAsync();
        try { await _session.RefreshQuotaAsync(); } catch (Exception ex) { _activity.Enqueue("Limites: " + ex.Message); }
        Save();
    }
    private bool SupportsCommand(string name) => !IsClaudeProvider || name is "new" or "resume" or "model" or "effort" or "status" or "stop" or "help" or "login" or "permissions" or "quit" or "diff";
}
