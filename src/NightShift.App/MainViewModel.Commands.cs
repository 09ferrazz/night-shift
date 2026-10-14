using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using NightShift.Core;
namespace NightShift.App;

public sealed class CommandAction(CommandDefinition definition, Func<bool> available, Func<bool>? supported = null, Func<string>? providerLabel = null) : INotifyPropertyChanged
{
    public string Name => definition.Name;
    public string Syntax => definition.Syntax;
    public string Title => definition.Name == "login" && providerLabel?.Invoke() == "Claude" ? "Entrar no Claude" : definition.Title;
    public string Description => supported?.Invoke() == false ? "Indisponível no " + providerLabel?.Invoke() + ". Este comando não será enviado como tarefa."
        : providerLabel?.Invoke() == "Claude" && definition.Name == "login" ? "Abra claude auth login no terminal para entrar na sua conta."
        : providerLabel?.Invoke() == "Claude" && definition.Name == "permissions" ? "Consulte revisão nativa e pedidos de permissão do Claude."
        : definition.Description;
    public string VisualCommand => definition.VisualCommand;
    public bool CanExecute => available();
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));
}

public sealed partial class MainViewModel
{
    private string _sessionSource = "night_shift";
    private string _historySearch = "";
    private bool _allProjects;
    private string? _historyCursor;
    private string? _loadedProject;
    private string _loadedSearch = "";
    private readonly HashSet<string> _historyCursors = [];
    private string _historyLabel = "Atualize para consultar também as conversas originais do CLI.";
    public IReadOnlyList<CommandAction> Commands { get; }
    public string HistorySearch { get => _historySearch; set { _historySearch = value; NotifyAll(); } }
    public bool AllProjects { get => _allProjects; set { _allProjects = value; NotifyAll(); } }
    public string HistoryLabel => _historyLabel;
    public bool CanUseShortcuts => !_busy && !_switchingProvider && !_session.IsBusy;
    public bool CanResumeLast => !IsSharedPlatform && CanUseShortcuts && Directory.Exists(ProjectPath);
    public bool CanLoadMoreHistory => CanUseShortcuts && _historyCursor is not null && Sessions.Count < 500;
    public bool CanExecuteCommand(string name, string argument = "")
    {
        if (!SupportsCommand(name)) return false;
        if (IsSharedPlatform && name is "resume" or "fork" or "review" or "compact" or "model" or "effort" or "login" or "init" or "rename") return false;
        if (name == "stop") return CanStop;
        if (_busy || _switchingProvider || _stopping) return false;
        if (name is "help" or "permissions" or "status" or "quit") return true;
        if (name is "fork" or "review" or "compact" or "rename")
            return !_session.IsBusy && _session.PendingRequests.Count == 0 && (_session.ThreadId is not null || name == "fork" && argument != "");
        return !_session.IsBusy;
    }
    public event Action<string>? PageRequested;
    public event Action? CloseRequested;

    private async Task EnsureConnectedAsync() { if (!IsConnected) await ConnectAsync(); }
    private void MergeRecent(IEnumerable<SessionRecord> records)
    {
        var merged = records.Where(r => r.Provider == Provider).Concat(RecentSessions.Where(r => r.Provider == Provider)).GroupBy(r => r.ThreadId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(r => r.UpdatedAt).First())
            .OrderByDescending(r => r.UpdatedAt).Take(100).ToArray();
        RecentSessions.Clear();
        foreach (var record in merged) RecentSessions.Add(record);
        NotifyAll();
    }
    private void UpsertRecent(SessionRecord record)
    {
        var old = RecentSessions.FirstOrDefault(r => r.ThreadId == record.ThreadId);
        if (old is not null) RecentSessions.Remove(old);
        RecentSessions.Insert(0, record);
        if (RecentSessions.Count > 100) RecentSessions.RemoveAt(RecentSessions.Count - 1);
    }
    public async Task RefreshRecentAsync()
    {
        if (IsSharedPlatform || !IsConnected) return;
        var page = await _session.ListSessionsAsync(Directory.Exists(ProjectPath) ? ProjectPath : null);
        MergeRecent(page.Sessions);
    }
    public async Task RefreshHistoryAsync(bool more = false)
    {
        if (IsSharedPlatform) { PageRequested?.Invoke("Live"); if (IsConnected) await RefreshLiveAsync(); return; }
        await EnsureConnectedAsync();
        if (!AllProjects && !Directory.Exists(ProjectPath)) throw new DirectoryNotFoundException("Escolha a pasta do projeto ou marque Todos os projetos.");
        var project = AllProjects ? null : Path.GetFullPath(ProjectPath); var search = HistorySearch.Trim();
        if (more && (project != _loadedProject || search != _loadedSearch)) more = false;
        var page = await _session.ListSessionsAsync(project, more ? _historyCursor : null, search);
        if (!more) { Sessions.Clear(); _historyCursors.Clear(); _loadedProject = project; _loadedSearch = search; }
        foreach (var record in page.Sessions.Where(r => r.Provider == Provider))
            if (Sessions.Count < 500 && !Sessions.Any(r => r.ThreadId == record.ThreadId)) Sessions.Add(record);
        _historyCursor = page.NextCursor is { } cursor && _historyCursors.Add(cursor) ? cursor : null;
        if (!more) foreach (var cached in _store.LoadSessions().Where(r => r.Provider == Provider))
            if (Sessions.Count < 500 && !Sessions.Any(r => r.ThreadId == cached.ThreadId) && (project is null || SessionCatalog.SameProject(project, cached.ProjectPath)) &&
                (search == "" || cached.Title.Contains(search, StringComparison.OrdinalIgnoreCase))) Sessions.Add(cached);
        MergeRecent(page.Sessions);
        _historyLabel = $"{Sessions.Count} conversas exibidas · " + (project is null ? "todos os projetos" : "pasta selecionada"); NotifyAll();
    }
    public async Task ResumeLastAsync()
    {
        if (!Directory.Exists(ProjectPath)) throw new DirectoryNotFoundException("Escolha a pasta do projeto antes de retomar.");
        await EnsureConnectedAsync();
        var record = await _session.LatestSessionAsync(ProjectPath) ?? throw new InvalidOperationException($"Nenhuma conversa do {ProviderLabel} encontrada nesta pasta. Confira se você escolheu a mesma raiz usada no CLI.");
        await ResumeAsync(record); PageRequested?.Invoke("Session");
        Notice = "Conversa retomada. Escreva a próxima tarefa ou envie continue. Iniciar turno ativa o acompanhamento noturno.";
    }
    private void AddSystemMessage(string text)
    {
        var entry = new ChatEntry { Role = "Night Shift", Text = text };
        _inbox[entry.Id] = entry; Flush(); PageRequested?.Invoke("Session");
    }
    public Task RunCommandAsync(string text) => ExecuteCommandAsync(CodexCommands.Parse(text));
    private async Task ExecuteCommandAsync(ParsedCommand command)
    {
        if (!command.IsCommand) throw new InvalidOperationException("Use um atalho da página Comandos.");
        if (command.Error is not null) throw new InvalidOperationException(command.Error);
        if (!SupportsCommand(command.Definition!.Name)) throw new InvalidOperationException($"/{command.Definition.Name} não está disponível no {ProviderLabel}. O rascunho foi mantido; use /help para ver os comandos suportados.");
        if (IsSharedPlatform && command.Definition!.Name is "resume" or "fork" or "review" or "compact" or "model" or "effort" or "login" or "init" or "rename")
            throw new InvalidOperationException("Use este comando no aplicativo de origem. No Night Shift, escolha a sessão em Sessões ao vivo.");
        if (_session.IsBusy && command.Definition!.Name is not ("stop" or "help" or "permissions" or "status" or "quit")) throw new InvalidOperationException("Aguarde o trabalho terminar ou clique em Parar.");
        var handlers = new Dictionary<string, Func<string, Task>> {
            ["new"] = async _ => { await NewSessionAsync(); PageRequested?.Invoke("Session"); },
            ["resume"] = async arg => {
                if (arg == "--last") await ResumeLastAsync();
                else { await EnsureConnectedAsync(); if (arg == "") { PageRequested?.Invoke("History"); await RefreshHistoryAsync(); }
                    else { await ResumeAsync(await _session.ReadSessionAsync(arg)); PageRequested?.Invoke("Session"); } }
            },
            ["fork"] = async arg => {
                await EnsureConnectedAsync(); Save();
                if (arg == "--last") await ResumeLastAsync(); else if (arg != "") await ResumeAsync(await _session.ReadSessionAsync(arg));
                await _session.ForkAsync(); _sessionSource = "night_shift"; _sessionTitle = "Cópia · " + _sessionTitle;
                ReplaceMessages(_session.RestoredHistory); _displayedThreadId = _session.ThreadId; UpdateSleep(); Save(); PageRequested?.Invoke("Session");
            },
            ["model"] = async arg => { await EnsureConnectedAsync(); if (arg == "") PageRequested?.Invoke("Settings");
                else { SelectedModel = Models.FirstOrDefault(m => m.Id.Equals(arg, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Modelo não disponível. Consulte Ajustes."); Save(); Notice = "Modelo aplicado ao próximo pedido."; } },
            ["effort"] = async arg => { await EnsureConnectedAsync(); if (arg == "") PageRequested?.Invoke("Settings");
                else { SelectedEffort = Efforts.FirstOrDefault(e => e.Equals(arg, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Esforço não disponível. Escolha um modelo em Ajustes."); Save(); Notice = "Esforço aplicado ao próximo pedido."; } },
            ["status"] = async _ => { if (IsConnected) await _session.RefreshQuotaAsync(); AddSystemMessage($"Projeto: {ProjectPath}\nConversa: {_session.ThreadId ?? "nova"}\nEstado: {_session.Status}\nModelo: {SelectedModel?.Name ?? "configuração local"}\nEsforço: {SelectedEffort}\n{BudgetLabel}\n{SecondaryBudgetLabel}\nTurno: {ShiftLabel}\nRetomada: {RetryLabel}\n{ReviewLabel}"); },
            ["review"] = async _ => { await EnsureConnectedAsync(); await _session.ReviewAsync(); UpdateSleep(); PageRequested?.Invoke("Session"); },
            ["compact"] = async _ => { await EnsureConnectedAsync(); await _session.CompactAsync(); UpdateSleep(); PageRequested?.Invoke("Session"); },
            ["diff"] = async _ => AddSystemMessage(await ProjectDiff.ReadAsync(ProjectPath)),
            ["mcp"] = async _ => { await EnsureConnectedAsync(); AddSystemMessage(await _session.McpStatusAsync()); },
            ["rename"] = async arg => { await EnsureConnectedAsync(); await _session.RenameAsync(arg); _sessionTitle = arg; Save(); Notice = "Nome salvo no histórico do " + ProviderLabel + "."; },
            ["permissions"] = _ => { PageRequested?.Invoke("Settings"); return Task.CompletedTask; },
            ["login"] = async _ => { await EnsureConnectedAsync(); await OpenLoginAsync(); },
            ["init"] = async _ => { await EnsureConnectedAsync(); await _session.SendAsync(ProjectPath, "Examine o projeto e crie ou melhore o AGENTS.md com instruções práticas para trabalhar nele. Preserve instruções existentes válidas e explique as mudanças.", SelectedModel?.Id ?? "", SelectedEffort); _displayedThreadId = _session.ThreadId; _sessionTitle = "Instruções do projeto"; PageRequested?.Invoke("Session"); },
            ["stop"] = _ => StopAsync(),
            ["help"] = _ => { PageRequested?.Invoke("Commands"); return Task.CompletedTask; },
            ["quit"] = _ => { CloseRequested?.Invoke(); return Task.CompletedTask; }
        };
        await handlers[command.Definition!.Name](command.Argument);
    }
    public async Task OpenLoginAsync()
    {
        var result = await _session.LoginAsync();
        if (IsClaudeProvider)
        {
            if (!result.TryGetProperty("terminalStarted", out var launched) || launched.ValueKind != System.Text.Json.JsonValueKind.True) throw new IOException("Não foi possível iniciar o login nativo do Claude.");
            Notice = "Conclua claude auth login no terminal aberto e reconecte para atualizar sua conta."; return;
        }
        var url = CodexSession.Text(result, "authUrl");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host == "chatgpt.com" || uri.Host == "auth.openai.com" || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("O Codex não retornou um endereço de login reconhecido.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Notice = "Conclua o login no navegador, desconecte e conecte novamente para atualizar os modelos e sua conta.";
    }
}
