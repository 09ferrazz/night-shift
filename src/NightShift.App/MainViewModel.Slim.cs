using NightShift.Core;

namespace NightShift.App;

public sealed partial class MainViewModel
{
    public double? SlimLeft => _settings.SlimLeft;
    public double? SlimTop => _settings.SlimTop;
    public bool SlimUsesCodexSharedEndpoint => IsCodexProvider && IsSharedPlatform;
    public bool SlimCanStop => !IsReadOnlySession && CanStop;
    public bool SlimCanContinue => !IsReadOnlySession && CanConfirmContinuation;
    public bool SlimCanEditDuration => !IsReadOnlySession;
    public bool SlimCanSetNightShift => !IsReadOnlySession && IsConnected && HasLiveSession && (!IsSharedPlatform || _session.CanAcceptSharedInput) && CanSetNightShift;
    public string SlimControlHint => IsReadOnlySession
        ? "Somente leitura: turno, continuar e parar não estão disponíveis. Use a sessão de origem."
        : !IsConnected ? "Conecte aqui para listar e vincular uma sessão."
        : !HasLiveSession ? IsSharedPlatform ? "Vincule uma sessão ao vivo para acompanhar o turno." : "Sua sessão gerenciada aparecerá aqui ao iniciar uma tarefa no aplicativo."
        : NightShiftEnabled ? "O turno acompanha esta mesma sessão." : "Ative o turno durante o trabalho ou a espera por limite.";
    public async Task SetSlimNightShiftAsync(bool enabled)
    {
        if (_disposed || _switchingProvider || _stopping || IsReadOnlySession) return;
        if (!enabled) { await SetNightShiftAsync(false); return; }
        if (!IsConnected || !HasLiveSession || _session.IsManualOperation) return;
        if (_session.IsShared)
        {
            if (!_session.CanAcceptSharedInput) return;
            // A linked origin has its own composer. Arm monitoring of that same
            // session now, without submitting a task or consuming our draft.
            _nightShiftRequested = false;
            if (!_session.Shift.Armed) _session.StartShift(ShiftHours);
            UpdateSleep(); NotifyAll();
            return;
        }
        await SetNightShiftAsync(true);
    }
    public void SaveSlimPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        _settings.SlimLeft = left; _settings.SlimTop = top;
        try { _store.SaveSettings(_settings); }
        catch (Exception ex) { Notice = "Não foi possível salvar a posição: " + ex.Message; }
    }
}
