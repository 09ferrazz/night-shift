using System.Collections.ObjectModel;
using System.IO;
using NightShift.Core;

namespace NightShift.App;

public sealed record PlatformOption(CodexPlatform Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel
{
    private static readonly PlatformOption[] CodexPlatforms = [new(CodexPlatform.Cli, "Codex local (CLI)"), new(CodexPlatform.Desktop, "Desktop · exp."), new(CodexPlatform.VsCode, "VS Code · exp.")];
    public PlatformOption[] Platforms => IsClaudeProvider ? [new(CodexPlatform.Cli, "Claude local (CLI)"), new(CodexPlatform.Desktop, "Claude externo · observar")] : CodexPlatforms;
    public ObservableCollection<SessionRecord> LiveSessions { get; } = [];
    public CodexPlatform Platform
    {
        get => IsClaudeProvider ? _settings.ClaudePlatform : _settings.Platform;
        set
        {
            if (!CanChangePlatform || !Enum.IsDefined(value)) return;
            if (IsClaudeProvider) _settings.ClaudePlatform = value; else _settings.Platform = value; NotifyAll();
        }
    }
    public string PlatformLabel => Platforms.FirstOrDefault(p => p.Value == Platform)?.Label ?? "CLI";
    public bool IsSharedPlatform => Platform != CodexPlatform.Cli;
    public bool IsCliPlatform => !IsSharedPlatform;
    public bool CanUseCliSettings => IsCliPlatform && IsConnected;
    public string SettingsConnectionHint => IsSharedPlatform ? "Modelo e permissões são definidos no aplicativo de origem. Duração e suspensão valem para o acompanhamento local." : "Modelo e revisão valem para o próximo pedido. Desconecte antes de trocar o executável.";
    public string ShiftActionLabel => CanStop ? "Parar" : "Iniciar turno";
    public bool CanToggleShift => CanStop || CanStartShift;
    public bool CanChangePlatform => !_busy && !_switchingProvider && !IsConnected;
    public bool CanDisconnect => !_busy && IsConnected;
    public bool CanRefreshLive => !_busy && IsConnected && IsSharedPlatform && !_session.Shift.Armed;
    public bool IsLiveEmpty => LiveSessions.Count == 0;
    public bool HasLiveSession => _session.ThreadId is not null;
    public bool HasQuotaNotice => _session.Shift.WaitingForQuota || _session.ContinuationReady;
    public bool CanConfirmContinuation => !_session.IsReadOnly && !_switchingProvider && !_busy && _session.ContinuationReady && !_session.IsBusy;
    public bool HasOriginRequest => _session.WaitingInOrigin && _session.PendingRequests.Count == 0;
    public bool IsTurnActive => _session.Shift.Armed || _session.IsBusy;
    public bool ShowStartShift => !CanStop;
    public string DataLocation => _store.Root;
    public string SharedEndpoint { get => _settings.SharedEndpoint; set { if (!IsConnected) { _settings.SharedEndpoint = value; NotifyAll(); } } }
    public string CompatibilityLabel => IsClaudeProvider && IsSharedPlatform ? IsConnected ? "Claude externo · somente observação" : "Claude externo · requer agents oficial" : !IsSharedPlatform ? "CLI · sessão gerenciada" : IsConnected
        ? HasLiveSession ? "Sessão ao vivo · conexão experimental" : "Servidor conectado · escolha uma sessão"
        : "Conexão compartilhada · experimental";
    public string IntegrationDetails => IsClaudeProvider ? IsSharedPlatform ? "Lista e observa sessões oficiais via claude agents. Não envia tarefas nem responde permissões de processos externos. Sessões background compatíveis podem abrir via claude attach no terminal." : "O Night Shift inicia uma sessão própria do Claude Code instalado, usando streaming e permissões nativas. Desktop, chat comum e Cowork não oferecem este conector." : IsSharedPlatform
        ? "Requer o mesmo app-server acessível ao Night Shift e ao aplicativo de origem. Use o socket local do Codex ou um WebSocket local em Opções de conexão. Instalações que usam um servidor privado não podem ser controladas por esta conexão."
        : "O Night Shift inicia uma sessão própria do Codex CLI instalado. Conversa, permissões e retomada após limite usam o protocolo oficial.";
    public string SharedLimitations => IsClaudeProvider ? "A observação exige uma versão com claude agents --json. Controle e permissões permanecem no terminal de origem. Histórico só deve ser retomado como sessão gerenciada após o processo anterior terminar." : "Conector experimental; Desktop e extensão ainda não validados de ponta a ponta. O reset é acompanhado, mas continuar exige confirmação: o protocolo não permite impedir atomicamente que outro cliente inicie uma nova tarefa. O CLI gerenciado mantém a retomada automática.";
    public string QuotaNotice => _session.ContinuationReady ? "Limite liberado. Confirme para continuar a tarefa interrompida." : "Limite de uso atingido · " + RetryLabel;
    public Task ConfirmContinuationAsync() => _session.ConfirmSharedContinuationAsync(CaptureContinuationDraft);

    public async Task RefreshLiveAsync()
    {
        var project = !AllProjects && Directory.Exists(ProjectPath) ? ProjectPath : null;
        var records = await _session.ListLiveSessionsAsync(project);
        LiveSessions.Clear(); foreach (var record in records.Where(r => r.Provider == Provider)) LiveSessions.Add(record);
        NotifyAll();
    }
    public async Task DisconnectAsync()
    {
        Save(); await _session.DisconnectAsync(); Flush(); ReplaceMessages([]);
        _displayedThreadId = null; LiveSessions.Clear(); _sessionTitle = "Nova sessão";
        _accountLabel = "Sua conta " + ProviderLabel; UpdateSleep(); NotifyAll();
    }
}
