using System.Text.Json;

namespace NightShift.Core;

public sealed partial class CodexSession
{
    public bool IsShared { get; private set; }
    public bool WaitingInOrigin { get; private set; }
    public bool CanAcceptSharedInput { get; private set; }
    public bool ContinuationReady { get; private set; }
    private string? _quotaTurnId;

    public async Task<IReadOnlyList<SessionRecord>> ListLiveSessionsAsync(string? project = null)
    {
        if (!IsShared) throw new InvalidOperationException("Conecte ao servidor compartilhado primeiro.");
        var records = new List<SessionRecord>(); var unavailable = 0;
        foreach (var id in await LoadedIdsAsync())
        {
            JsonElement result;
            try { result = await Rpc.RequestAsync("thread/read", new { threadId = id, includeTurns = false }, TimeSpan.FromSeconds(15)); }
            catch (RpcException ex) when (IsUnavailableThread(ex)) { unavailable++; continue; }
            if (!result.TryGetProperty("thread", out var thread) || SessionCatalog.ReadThread(thread) is not { } record) continue;
            if (project is not null && !SessionCatalog.SameProject(record.ProjectPath, project)) continue;
            record.Originator = Text(thread, "originator") ?? record.SourceLabel;
            record.RuntimeStatus = thread.TryGetProperty("status", out var status) ? Text(status, "type") ?? "" : "";
            if (record.RuntimeStatus == "notLoaded") continue;
            records.Add(record);
        }
        if (unavailable > 0) Warning?.Invoke($"{unavailable} sessão(ões) ainda sem histórico ou já removida(s). As demais continuam disponíveis; atualize a lista para tentar novamente.");
        return records.OrderByDescending(r => r.UpdatedAt).ToArray();
    }

    private static bool IsUnavailableThread(RpcException error) => (error.Code is -32600 or -32000 &&
        (error.Message.StartsWith("no rollout found for thread id ", StringComparison.OrdinalIgnoreCase) ||
         error.Message.StartsWith("thread not found:", StringComparison.OrdinalIgnoreCase) ||
         error.Message.StartsWith("thread has no persisted rollout", StringComparison.OrdinalIgnoreCase))) ||
        (error.Code == -32603 && error.Message.StartsWith("failed to read thread:", StringComparison.OrdinalIgnoreCase) &&
         error.Message.Contains(": rollout at ", StringComparison.OrdinalIgnoreCase) && error.Message.EndsWith(" is empty", StringComparison.OrdinalIgnoreCase));

    private async Task<HashSet<string>> LoadedIdsAsync()
    {
        var ids = new HashSet<string>(); var cursors = new HashSet<string>(); string? cursor = null;
        do
        {
            var result = await Rpc.RequestAsync("thread/loaded/list", new { cursor, limit = 100 }, TimeSpan.FromSeconds(15));
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new IOException("O servidor não retornou a lista de sessões carregadas.");
            foreach (var id in data.EnumerateArray()) if (id.ValueKind == JsonValueKind.String && id.GetString() is { } value) ids.Add(value);
            cursor = Text(result, "nextCursor");
        } while (cursor is not null && cursors.Add(cursor) && ids.Count < 500);
        return ids;
    }

    public async Task<JsonElement> AttachLiveAsync(SessionRecord record)
    {
        if (!IsShared) throw new InvalidOperationException("Esta conexão não é compartilhada.");
        await _gate.WaitAsync();
        try
        {
            if (Shift.Armed) throw new InvalidOperationException("Pare o acompanhamento antes de trocar de sessão.");
            if (!(await LoadedIdsAsync()).Contains(record.ThreadId)) throw new InvalidOperationException("Esta conversa não está carregada no servidor. Abra-a no aplicativo de origem e atualize a lista.");
            var existing = await Rpc.RequestAsync("thread/read", new { threadId = record.ThreadId, includeTurns = false });
            var before = existing.GetProperty("thread");
            if (before.TryGetProperty("status", out var state) && Text(state, "type") == "notLoaded")
                throw new InvalidOperationException("A sessão foi descarregada. Abra-a no aplicativo de origem.");
            if (SessionCatalog.ReadThread(before) is not { } canonical) throw new IOException("Sessão sem pasta válida.");
            if (ThreadId is not null && ThreadId != record.ThreadId) await UnsubscribeAsync();
            ClearSharedAttachment(); _loadingThreadId = record.ThreadId;
            var result = await Rpc.RequestAsync("thread/resume", new { threadId = record.ThreadId });
            var thread = result.GetProperty("thread");
            ThreadId = Text(thread, "id");
            if (ThreadId != record.ThreadId) { ThreadId = null; throw new IOException("O servidor respondeu com outra sessão."); }
            _project = canonical.ProjectPath; _firstPrompt = false; _items.Clear(); _quotaTurnId = null; _usageFailure = false;
            RestoredHistory = await LoadHistoryAsync(result, ThreadId!);
            foreach (var entry in RestoredHistory.Where(e => e.Role == "Codex"))
                _items[entry.Id] = new ChatEntry { Id = entry.Id, Role = entry.Role, Text = entry.Text, Time = entry.Time };
            CanAcceptSharedInput = !thread.TryGetProperty("canAcceptDirectInput", out var accepts) || accepts.ValueKind != JsonValueKind.False;
            _turn = null; IsBusy = false; WaitingInOrigin = false;
            if (thread.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
                foreach (var turn in turns.EnumerateArray()) if (Text(turn, "status") == "inProgress") { _turn = Text(turn, "id"); IsBusy = true; }
            if (thread.TryGetProperty("status", out var runtime)) ApplySharedStatus(runtime);
            SetStatus(PendingRequests.Count > 0 || WaitingInOrigin ? "Precisa de você" : IsBusy ? "Acompanhando ao vivo" : "Sessão ao vivo conectada");
            if (!IsBusy && thread.TryGetProperty("turns", out var history) && history.ValueKind == JsonValueKind.Array && history.GetArrayLength() > 0)
            {
                var last = history[history.GetArrayLength() - 1];
                if (Text(last, "status") == "failed" && last.TryGetProperty("error", out var error) && IsUsageLimit(error))
                { _quotaTurnId = Text(last, "id"); await WaitForQuotaAsync(); }
            }
            Activity?.Invoke("Sessão assinada no servidor existente. Políticas e configuração preservadas.");
            return result;
        }
        catch
        {
            if (_loadingThreadId is not null) ClearSharedAttachment();
            throw;
        }
        finally { _loadingThreadId = null; _gate.Release(); }
    }

    private void ClearSharedAttachment()
    {
        ThreadId = null; _turn = null; IsBusy = false; CanAcceptSharedInput = false; WaitingInOrigin = false;
        ContinuationReady = false; _quotaTurnId = null; RestoredHistory = []; _items.Clear(); ClearPending();
        SetStatus("Escolha uma sessão ao vivo");
    }

    public async Task ConfirmSharedContinuationAsync(Func<ContinuationDraft>? draftProvider = null)
    {
        var version = Volatile.Read(ref _stopVersion);
        Interlocked.Increment(ref _sending); Changed?.Invoke();
        await _gate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _stopVersion)) return;
            if (!IsShared || !ContinuationReady || IsBusy || WaitingInOrigin || PendingRequests.Count > 0)
                throw new InvalidOperationException("Confira o estado da conversa antes de continuar.");
            if (!await SharedRetryIsCurrentAsync()) { ContinuationReady = false; return; }
            await RefreshQuotaAsync();
            if (version != Volatile.Read(ref _stopVersion)) return;
            if (!Rates.Known || Rates.IsBlocked) throw new InvalidOperationException("O limite ainda não está disponível. Atualize o acompanhamento.");
            ContinuationReady = false;
            await StartContinuationAsync(version, false, draftProvider);
        }
        finally { Interlocked.Decrement(ref _sending); _gate.Release(); Changed?.Invoke(); }
    }

    private void ApplySharedStatus(JsonElement status)
    {
        var type = Text(status, "type");
        WaitingInOrigin = status.TryGetProperty("activeFlags", out var flags) && flags.ValueKind == JsonValueKind.Array &&
            flags.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() is "waitingOnApproval" or "waitingOnUserInput");
        if (type == "active") IsBusy = true;
        if (type is "idle" or "notLoaded" or "systemError") { IsBusy = false; _turn = null; }
        if (type is "notLoaded" or "systemError") { Shift.Stop(); CanAcceptSharedInput = false; }
        if (WaitingInOrigin) SetStatus("Precisa de você");
        else if (type == "active") SetStatus("Acompanhando ao vivo");
        Changed?.Invoke();
    }

    private async Task<bool> SharedRetryIsCurrentAsync()
    {
        var result = await Rpc.RequestAsync("thread/read", new { threadId = ThreadId, includeTurns = true });
        var thread = result.GetProperty("thread");
        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() == 0)
        { Shift.Stop(); SetStatus("Confirme a continuação no aplicativo de origem"); return false; }
        var last = turns[turns.GetArrayLength() - 1];
        bool current = _quotaTurnId is not null && Text(last, "id") == _quotaTurnId && Text(last, "status") == "failed" &&
            last.TryGetProperty("error", out var error) && IsUsageLimit(error);
        if (thread.TryGetProperty("status", out var status) && Text(status, "type") != "idle") current = false;
        if (!current)
        {
            ContinuationReady = false; Shift.Stop(); SetStatus("Conversa atualizada no aplicativo de origem");
            Activity?.Invoke("Retomada cancelada: o servidor não confirmou que a última tarefa ainda está interrompida por limite.");
        }
        return current;
    }

    private async Task UnsubscribeAsync()
    {
        if (ThreadId is not null && IsConnected)
            await Rpc.RequestAsync("thread/unsubscribe", new { threadId = ThreadId }, TimeSpan.FromSeconds(5));
    }
    private async Task DetachLiveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Shift.Stop(); Interlocked.Increment(ref _stopVersion); await UnsubscribeAsync();
            ClearSharedAttachment();
        }
        finally { _gate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        Shift.Stop(); Interlocked.Increment(ref _stopVersion);
        await _gate.WaitAsync();
        try
        {
            Interlocked.Increment(ref _connectionVersion);
            if (_rpc is not null) { await _rpc.DisposeAsync(); _rpc = null; }
            ThreadId = null; _turn = null; RestoredHistory = []; _items.Clear(); IsBusy = false; WaitingInOrigin = false;
            CanAcceptSharedInput = false; ContinuationReady = false; _quotaTurnId = null; ClearPending(); SetStatus("Desconectado");
        }
        finally { _gate.Release(); }
    }
}
