using System.Text.Json;
namespace NightShift.Core;

public sealed partial class CodexSession
{
    public async Task<SessionPage> ListSessionsAsync(string? project = null, string? cursor = null, string? search = null)
    {
        var cwd = string.IsNullOrWhiteSpace(project) ? null : Path.GetFullPath(project);
        var result = await Rpc.RequestAsync("thread/list", new { cwd, cursor, limit = 50, sortKey = "updated_at", sortDirection = "desc",
            sourceKinds = new[] { "cli", "vscode", "appServer" }, searchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim() });
        return SessionCatalog.Read(result, cwd);
    }
    public async Task<SessionRecord?> LatestSessionAsync(string project)
    {
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException("Selecione a pasta do projeto antes de retomar.");
        string? cursor = null; var seen = new HashSet<string>();
        for (var page = 0; page < 10; page++)
        {
            var result = await ListSessionsAsync(project, cursor);
            if (result.Sessions.Count > 0) return result.Sessions.OrderByDescending(s => s.UpdatedAt).First();
            cursor = result.NextCursor;
            if (cursor is null || !seen.Add(cursor)) break;
        }
        return null;
    }
    public async Task<SessionRecord> ReadSessionAsync(string id)
    {
        var result = await Rpc.RequestAsync("thread/read", new { threadId = id, includeTurns = false });
        return result.TryGetProperty("thread", out var thread) && SessionCatalog.ReadThread(thread) is { } record ? record
            : throw new InvalidOperationException("O Codex não retornou uma conversa com pasta válida.");
    }
    private async Task<IReadOnlyList<ChatEntry>> LoadHistoryAsync(JsonElement result, string id)
    {
        var inline = ThreadHistory.Read(result);
        var partial = Text(result, "turnsBackwardsCursor") is not null || Text(result, "itemsBackwardsCursor") is not null ||
            result.TryGetProperty("thread", out var thread) && thread.TryGetProperty("turns", out var initialTurns) &&
            initialTurns.ValueKind == JsonValueKind.Array && initialTurns.EnumerateArray().Any(t => Text(t, "itemsView") is "summary" or "notLoaded");
        if (inline.Count > 0 && !partial) return inline;
        try
        {
            var pages = new List<JsonElement[]>(); string? cursor = null; var seen = new HashSet<string>();
            do
            {
                var page = await Rpc.RequestAsync("thread/turns/list", new { threadId = id, cursor, limit = 50, sortDirection = "desc", itemsView = "full" });
                if (!page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new IOException("Página de histórico inválida.");
                var turns = data.EnumerateArray().Take(50).Select(t => t.Clone()).ToArray();
                if (turns.Any(t => Text(t, "itemsView") is "summary" or "notLoaded")) throw new IOException("O servidor não retornou o histórico completo solicitado.");
                pages.Add(turns);
                cursor = Text(page, "nextCursor");
                if (cursor is not null && !seen.Add(cursor))
                {
                    Warning?.Invoke("O Codex repetiu uma página do histórico. A transcrição carregada pode estar incompleta.");
                    break;
                }
            }
            while (cursor is not null);
            var loaded = ThreadHistory.ReadTurns(JsonSerializer.SerializeToElement(pages.AsEnumerable().Reverse().SelectMany(p => p.Reverse())));
            if (loaded.Count == 0 && inline.Count > 0) throw new IOException("O servidor retornou apenas a prévia do histórico.");
            return loaded;
        }
        catch (RpcException ex) when (ex.Code is -32601 or -32600 || ex.Message.Contains("experimental", StringComparison.OrdinalIgnoreCase))
        {
            try { return ThreadHistory.Read(await Rpc.RequestAsync("thread/read", new { threadId = id, includeTurns = true })); }
            catch (Exception fallbackError) when (fallbackError is RpcException or IOException or TimeoutException)
            { Warning?.Invoke("A conversa foi retomada, mas a transcrição não está disponível. Atualize o CLI para exibir o histórico."); return []; }
        }
        catch (Exception ex) when (ex is RpcException or IOException or TimeoutException)
        { Warning?.Invoke("A conversa foi retomada, mas a transcrição pode estar incompleta: " + ex.Message); return inline; }
    }
    private void RequireIdleThread()
    {
        if (IsBusy || PendingRequests.Count > 0) throw new InvalidOperationException("Aguarde o trabalho terminar ou clique em Parar.");
        if (ThreadId is null) throw new InvalidOperationException("Envie uma tarefa ou retome uma conversa primeiro.");
    }
    public async Task ForkAsync()
    {
        await _gate.WaitAsync();
        try
        {
            RequireIdleThread(); Shift.Stop(); Interlocked.Increment(ref _stopVersion); ClearPending();
            _forkParentThreadId = ThreadId; _loadingThreadId = null; _forking = true;
            var result = await Rpc.RequestAsync("thread/fork", new { threadId = ThreadId, cwd = _project,
                approvalPolicy = "on-request", approvalsReviewer = _settings.AutoReview ? "auto_review" : "user", sandbox = "workspace-write" });
            ThreadId = result.GetProperty("thread").GetProperty("id").GetString() ?? throw new IOException("Bifurcação sem identificador.");
            _loadingThreadId = ThreadId; _forking = false;
            lock (_pendingGate) _pending.RemoveAll(r => Text(r.Parameters, "threadId") is { } thread && thread != ThreadId);
            _items.Clear(); _firstPrompt = false; IsManualOperation = false;
            RestoredHistory = await LoadHistoryAsync(result, ThreadId); SetStatus(PendingRequests.Count > 0 ? "Precisa de você" : "Pronto"); Activity?.Invoke("Nova conversa criada a partir da anterior.");
        }
        finally { _forking = false; _loadingThreadId = null; _forkParentThreadId = null; _gate.Release(); }
    }
    public Task ReviewAsync() => StartManualAsync("review/start", new { threadId = ThreadId, target = new { type = "uncommittedChanges" }, delivery = "inline" }, "Revisando alterações");
    public Task CompactAsync() => StartManualAsync("thread/compact/start", new { threadId = ThreadId }, "Compactando contexto");
    private async Task StartManualAsync(string method, object parameters, string label)
    {
        await _gate.WaitAsync();
        try
        {
            RequireIdleThread(); Shift.Stop(); Interlocked.Increment(ref _stopVersion); IsManualOperation = true; _usageFailure = false;
            IsBusy = true; SetStatus(label); Activity?.Invoke(label + ". O turno automático foi desativado.");
            try {
                var result = await Rpc.RequestAsync(method, parameters);
                if (result.TryGetProperty("turn", out var turn)) _turn = Text(turn, "id");
            }
            catch { IsBusy = false; IsManualOperation = false; _turn = null; SetStatus("Precisa de atenção"); throw; }
        }
        finally { _gate.Release(); }
    }
    public async Task RenameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160) throw new ArgumentException("Use um nome com 1 a 160 caracteres.");
        await _gate.WaitAsync();
        try { RequireIdleThread(); await Rpc.RequestAsync("thread/name/set", new { threadId = ThreadId, name = name.Trim() }); }
        finally { _gate.Release(); }
    }
    public async Task<string> McpStatusAsync()
    {
        var rows = new List<string>(); string? cursor = null; var seen = new HashSet<string>();
        for (var n = 0; n < 10; n++)
        {
            var result = await Rpc.RequestAsync("mcpServerStatus/list", new { cursor, limit = 50 });
            if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var server in data.EnumerateArray().Take(50)) rows.Add($"• {Text(server, "name") ?? "MCP"} · autenticação: {Text(server, "authStatus") ?? "desconhecida"}");
            cursor = Text(result, "nextCursor"); if (cursor is null || !seen.Add(cursor)) break;
        }
        return rows.Count == 0 ? "Nenhuma integração MCP configurada nesta instalação do Codex." : string.Join("\n", rows);
    }
}
