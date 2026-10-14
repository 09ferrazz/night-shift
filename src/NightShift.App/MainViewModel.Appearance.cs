using NightShift.Core;

namespace NightShift.App;

public sealed partial class MainViewModel
{
    private bool _nightShiftRequested;
    public bool IsDarkTheme
    {
        get => _settings.DarkTheme;
        set
        {
            if (_settings.DarkTheme == value) return;
            _settings.DarkTheme = value;
            try { _store.SaveSettings(_settings); }
            catch (Exception ex) { Notice = "Não foi possível salvar o tema: " + ex.Message; }
            Notify(nameof(IsDarkTheme));
        }
    }
    public string ConversationTitle => string.IsNullOrWhiteSpace(_sessionTitle) || _sessionTitle == "Nova sessão" ? "Nova conversa" : _sessionTitle;
    public string? CurrentThreadId => _session.ThreadId;
    public bool CanOpenRecent => IsSharedPlatform ? CanRefreshLive : CanUseShortcuts;
    public string SidebarSectionLabel => IsSharedPlatform ? "Sessões ao vivo" : "Recentes";
    public string HeaderConnectionLabel => IsConnected ? "Conectado" : "Conectar";
    public bool ShowStopAction => !_session.IsReadOnly && (_stopping || _session.IsBusy || _session.IsPreparing || _session.Shift.WaitingForQuota || _session.ContinuationReady || HasPending);
    public string PrimaryActionLabel => "Enviar";
    public bool CanPrimaryAction => CanSend;
    public bool NightShiftEnabled => _session.Shift.Armed || _nightShiftRequested;
    public bool CanSetNightShift => !_session.IsReadOnly && (_session.Shift.Armed ? CanStop : !_busy && !_switchingProvider && !_stopping && !_session.IsManualOperation);
    public string NightShiftDetail => _session.Shift.Armed ? ShiftLabel : _nightShiftRequested ? $"Ao enviar · até {ShiftHours} h" : "";

    public async Task SetNightShiftAsync(bool enabled)
    {
        if (_stopping) return;
        if (!enabled)
        {
            _nightShiftRequested = false;
            if (_session.Shift.Armed) await StopAsync();
        }
        else if (!_session.Shift.Armed)
        {
            if (_session.IsBusy || _session.Shift.WaitingForQuota)
            {
                _session.StartShift(ShiftHours); _nightShiftRequested = false; UpdateSleep();
            }
            else _nightShiftRequested = true;
        }
        NotifyAll();
    }
    public Task SendPrimaryAsync() => _nightShiftRequested && !_session.Shift.Armed && !CodexCommands.Parse(Draft).IsCommand
        ? StartShiftAsync() : SendAsync();
    private ContinuationDraft CaptureContinuationDraft()
    {
        var revision = _draftRevision;
        var attached = Attachments.ToArray();
        PinAttachments(attached);
        var accepted = false;
        return new(Draft, () => { accepted = true; AcceptDraft(revision, attached); }, attached,
            () => ReleaseAttachments(attached, accepted));
    }
    public bool WillIncludeDraft => HasQuotaNotice && HasDraft
        && (_session.Shift.Armed || _session.ContinuationReady);
    public string DraftDeliveryHint => WillIncludeDraft ? "Seu rascunho será incluído na retomada" : "Ctrl+Enter · enviar";
    private bool _refreshingLimits;
    private string _limitsError = "";
    private RateWindow? LimitWindow(int minutes) => _session.Rates.Primary?.DurationMinutes == minutes ? _session.Rates.Primary
        : _session.Rates.Secondary?.DurationMinutes == minutes ? _session.Rates.Secondary : null;
    private RateWindow? FiveHourLimit => LimitWindow(300);
    private RateWindow? WeeklyLimit => LimitWindow(10080);
    public bool HasFiveHourLimit => FiveHourLimit is not null;
    public bool HasWeeklyLimit => WeeklyLimit is not null;
    public double FiveHourUsed => FiveHourLimit?.UsedPercent ?? 0;
    public double WeeklyUsed => WeeklyLimit?.UsedPercent ?? 0;
    public string FiveHourUsage => LimitUsage(FiveHourLimit);
    public string WeeklyUsage => LimitUsage(WeeklyLimit);
    private static string LimitUsage(RateWindow? window) => window is null ? "Não informado" : $"{window.UsedPercent:0.#}% usado";
    private static string LimitReset(RateWindow? window) => window?.ResetsAt is not { } reset ? "Reset não informado"
        : reset <= DateTimeOffset.UtcNow ? "Reset previsto já passou · atualize" : "Reset em " + reset.ToLocalTime().ToString("dd/MM HH:mm");
    public string FiveHourReset => LimitReset(FiveHourLimit);
    public string WeeklyReset => LimitReset(WeeklyLimit);
    public bool CanRefreshLimits => IsConnected && !_refreshingLimits;
    public string LimitsUpdateLabel => !IsConnected ? "Sem conexão · últimos dados recebidos"
        : _refreshingLimits ? "Atualizando…" : _limitsError.Length > 0 ? _limitsError
        : _session.RatesUpdatedAt is { } at ? "Dados do " + ProviderLabel + " · " + at.ToLocalTime().ToString("HH:mm:ss") : "Aguardando dados do " + ProviderLabel;
    public async Task RefreshLimitsAsync()
    {
        if (!CanRefreshLimits) return;
        _refreshingLimits = true; _limitsError = ""; NotifyAll();
        try { await _session.RefreshQuotaAsync(); }
        catch { _limitsError = "Não foi possível atualizar · tente novamente"; }
        finally { _refreshingLimits = false; NotifyAll(); }
    }
}
