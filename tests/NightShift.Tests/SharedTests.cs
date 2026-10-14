using System.Diagnostics;
using System.Text.Json;
using NightShift.Core;

internal static class SharedTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Stop during shared continuation preparation prevents a late turn", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-shared-stop-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new NightShift.App.MainViewModel(new AppStore(folder), session);
            try
            {
                await session.ConnectSharedAsync(Server(true), new());
                await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
                await session.StopAsync(); session.StartShift(8);
                await session.SendAsync(Path.GetTempPath(), "quota", "", "");
                await Until(() => session.Shift.WaitingForQuota);
                await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(6));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Activity += line => { if (line.Contains("confirm-limit-pending")) entered.TrySetResult(); };
                var confirm = session.ConfirmSharedContinuationAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var enabled = view.CanStop;
                await session.StopAsync(); await confirm;
                Check(enabled, "Stop was unavailable while confirming shared continuation");
                Check(session.TurnsStarted == 1 && !session.ContinuationReady, "Shared continuation started after Stop");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Platform cannot change while connected and shared controls require attachment", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "platform-" + Guid.NewGuid());
            var session = new CodexSession();
            await using var view = new NightShift.App.MainViewModel(new AppStore(folder), session);
            try
            {
                view.Platform = CodexPlatform.Desktop;
                await session.ConnectSharedAsync(Server(), new());
                view.Platform = CodexPlatform.Cli;
                Check(view.Platform == CodexPlatform.Desktop, "live transport relabeled as CLI");
                view.Draft = "test"; view.ProjectPath = Path.GetTempPath();
                Check(!view.CanSend && !view.CanStartShift, "unattached shared controls enabled");
                await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
                Check(view.CanConnect, "Disconnect button disabled during external turn");
                await view.DisconnectAsync(); view.Platform = CodexPlatform.VsCode;
                Check(view.Platform == CodexPlatform.VsCode && !view.IsConnected, "switch after disconnect");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Shared connection lists only loaded sessions and preserves external policies", async () =>
        {
            await using var session = new CodexSession();
            await session.ConnectSharedAsync(Server(), new());
            var live = await session.ListLiveSessionsAsync(Path.GetTempPath());
            Check(live.Count == 1 && live[0].ThreadId == "live-thread", "loaded catalog");
            await session.AttachLiveAsync(live[0]);
            Check(session.IsBusy, "active turn must remain busy after attach");
            Check(session.RestoredHistory.Single().Text == "Trabalho iniciado no editor", "canonical transcript");
            await session.StopAsync();
            Check(!session.IsBusy && !session.Shift.Armed, "interrupt selected turn");
            await session.SendAsync(Path.GetTempPath(), "continue", "ignored-model", "high");
            await Until(() => session.Status == "Concluído");
            Check(session.TurnsStarted == 1, "send over existing session");
        });
        yield return ("Shared attach rejects stored-only conversations", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            try { await session.AttachLiveAsync(new() { ThreadId = "saved-only", ProjectPath = Path.GetTempPath() }); }
            catch (InvalidOperationException) { Check(session.ThreadId is null, "no attachment on failure"); return; }
            throw new Exception("Stored conversation was advertised as live");
        });
        yield return ("Shared sends show one canonical user message", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            var messages = new Dictionary<string, ChatEntry>();
            session.ChatChanged += entry => { if (entry.Role == "Você") messages[entry.Id] = entry; };
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            await session.StopAsync(); await session.SendAsync(Path.GetTempPath(), "hello", "", "");
            await Until(() => session.Status == "Concluído");
            Check(messages.Count == 1 && messages.Single().Value.Text == "hello", "duplicate optimistic message");
        });
        yield return ("Shared delta continues the partial assistant message restored on attach", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            string? visible = null; session.ChatChanged += entry => { if (entry.Id == "external-message") visible = entry.Text; };
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            await Until(() => visible is not null);
            Check(visible == "Trabalho iniciado no editor e continuado", "partial prefix was lost: " + visible);
        });
        yield return ("Shared requests are scoped and resolved through the original connection", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            await Until(() => session.PendingRequests.Count == 1);
            Check(session.PendingRequests[0].Key == "\"approval-live\"", "foreign request leaked");
            await session.ResolveRequestAsync(session.PendingRequests[0], new { decision = "decline" });
            await Until(() => session.Status == "Concluído");
            Check(session.PendingRequests.Count == 0, "permission reply not sent");
        });
        yield return ("Shared disconnect does not interrupt someone else's turn", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            await session.DisconnectAsync();
            Check(!session.IsConnected && !session.IsBusy && session.ThreadId is null, "stale shared state");
        });
        yield return ("Shared quota announces reset without automatically steering an external task", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            await session.StopAsync(); session.StartShift(8);
            await session.SendAsync(Path.GetTempPath(), "quota", "", "");
            await Until(() => session.Shift.WaitingForQuota);
            await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(6));
            await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(12));
            Check(session.TurnsStarted == 1 && session.ContinuationReady && !session.Shift.Armed, "shared continuation submitted without confirmation");
            await session.ConfirmSharedContinuationAsync();
            await Until(() => session.Status == "Concluído");
            Check(session.TurnsStarted == 2 && !session.ContinuationReady, "explicit confirmation did not continue once");
        });
        yield return ("Shared quota abandons retry when another client advanced the thread", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            await session.StopAsync(); session.StartShift(8);
            await session.SendAsync(Path.GetTempPath(), "quota-external", "", "");
            await Until(() => session.Shift.WaitingForQuota);
            await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(6));
            Check(session.TurnsStarted == 1 && !session.Shift.WaitingForQuota, "stale task was continued");
        });
        yield return ("Shared continuation includes the draft only after explicit confirmation", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-shared-draft-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new NightShift.App.MainViewModel(new AppStore(folder), session);
            try
            {
                await session.ConnectSharedAsync(Server(), new());
                await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
                await session.StopAsync(); session.StartShift(8);
                await session.SendAsync(Path.GetTempPath(), "quota", "", "");
                await Until(() => session.Shift.WaitingForQuota);
                view.Draft = "Include this after confirmation";
                await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(6));
                Check(session.TurnsStarted == 1 && view.Draft.Length > 0, "Draft sent without confirmation");
                string? canonical = null;
                session.ChatChanged += entry => { if (entry.Role == "Você") canonical = entry.Text; };
                await view.ConfirmContinuationAsync();
                await Until(() => session.Status == "Concluído");
                Check(view.Draft == "" && canonical == Protocol.ContinueWithDraft("Include this after confirmation"), "Confirmed continuation lost or duplicated the draft");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Shared endpoint rejects remote hosts and embedded secrets", () =>
        {
            foreach (var address in new[] { "ws://example.com:4500", "http://127.0.0.1:4500", "ws://user:secret@localhost:4500", "ws://localhost:4500/?token=secret" })
            {
                try { SharedConnection.ParseWebSocket(address); }
                catch (ArgumentException) { continue; }
                throw new Exception("Unsafe endpoint accepted: " + address);
            }
            Check(SharedConnection.ParseWebSocket("ws://127.0.0.1:4500").IsLoopback, "local endpoint rejected");
            return Task.CompletedTask;
        });
        yield return ("Failed shared switch clears the unsubscribed session", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server(), new());
            await session.AttachLiveAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
            try { await session.AttachLiveAsync(new() { ThreadId = "broken-thread", ProjectPath = Path.GetTempPath() }); }
            catch (RpcException) { Check(session.ThreadId is null && !session.IsBusy && !session.CanAcceptSharedInput && session.PendingRequests.Count == 0, "old unsubscribed session remains actionable"); return; }
            throw new Exception("Expected failed switch");
        });
    }
    private static ProcessStartInfo Server(bool slowConfirm = false)
    {
        var p = new ProcessStartInfo(Environment.ProcessPath!);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) p.ArgumentList.Add(typeof(SharedTests).Assembly.Location);
        p.ArgumentList.Add("--shared-server"); if (slowConfirm) p.ArgumentList.Add("--slow-confirm"); return p;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    public static async Task RunServerAsync()
    {
        string turnId = "external-1", status = "inProgress"; int turns = 0, rateReads = 0; bool quota = false, advanced = false;
        async Task Emit(object value) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); }
        object Thread() => new { id = "live-thread", cwd = Path.GetTempPath(), source = "vscode", originator = "codex_vscode", name = "Editor", updatedAt = 2000000000,
            canAcceptDirectInput = true, status = new { type = status == "inProgress" ? "active" : "idle", activeFlags = Array.Empty<string>() },
            turns = new[] { new { id = advanced ? "another-client-turn" : turnId, status = advanced ? "completed" : status,
                error = quota && !advanced ? new { message = "Usage exhausted", codexErrorInfo = "usageLimitExceeded" } : null,
                items = new[] { new { id = "external-message", type = "agentMessage", text = "Trabalho iniciado no editor" } } } } };
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line); var m = doc.RootElement;
            if (!m.TryGetProperty("method", out var method))
            {
                Check(m.GetProperty("id").GetString() == "approval-live", "wrong request id");
                Check(m.GetProperty("result").GetProperty("decision").GetString() == "decline", "wrong reply");
                status = "completed";
                await Emit(new { method = "turn/completed", @params = new { threadId = "live-thread", turn = new { id = turnId, status } } }); continue;
            }
            if (!m.TryGetProperty("id", out var id)) continue;
            var p = m.GetProperty("params"); object result = new { };
            switch (method.GetString())
            {
                case "initialize": result = new { userAgent = "codex-test/0.158.0" }; break;
                case "thread/loaded/list": result = new { data = new[] { "live-thread", "broken-thread" }, nextCursor = (string?)null }; break;
                case "thread/read": result = p.GetProperty("threadId").GetString() == "broken-thread" ? new { thread = new { id = "broken-thread", cwd = Path.Combine(Path.GetTempPath(), "other-project"), status = new { type = "idle" } } } : (object)new { thread = Thread() }; break;
                case "thread/resume":
                    if (p.GetProperty("threadId").GetString() == "broken-thread") { await Emit(new { id, error = new { code = -32000, message = "Thread unloaded during attach" } }); continue; }
                    Check(p.EnumerateObject().All(x => x.Name == "threadId"), "external policies overridden");
                    result = new { thread = Thread() }; break;
                case "turn/interrupt":
                    Check(p.GetProperty("turnId").GetString() == turnId, "wrong interrupted turn"); status = "interrupted"; break;
                case "thread/unsubscribe": break;
                case "account/rateLimits/read":
                    if (++rateReads == 3 && Environment.GetCommandLineArgs().Contains("--slow-confirm"))
                    { await Console.Error.WriteLineAsync("confirm-limit-pending"); await Console.Error.FlushAsync(); await Task.Delay(350); }
                    result = new { rateLimits = new { primary = new { usedPercent = 10, resetsAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } } }; break;
                case "turn/start":
                    Check(p.EnumerateObject().All(x => x.Name is "threadId" or "input"), "external turn policies overridden");
                    Check(p.GetProperty("threadId").GetString() == "live-thread", "wrong thread");
                    turns++; turnId = "night-" + turns; var text = p.GetProperty("input")[0].GetProperty("text").GetString()!;
                    quota = turns == 1 && text.StartsWith("quota"); advanced = text == "quota-external";
                    status = quota ? "failed" : "completed";
                    await Emit(new { id, result = new { turn = new { id = turnId, status = "inProgress" } } });
                    await Emit(new { method = "item/started", @params = new { threadId = "live-thread", item = new { id = "user-" + turns, type = "userMessage", content = new[] { new { type = "text", text } } } } });
                    await Emit(new { method = "turn/completed", @params = new { threadId = "live-thread", turn = new { id = turnId, status, error = quota ? new { message = "Limit", codexErrorInfo = "usageLimitExceeded" } : null } } }); continue;
                default: await Emit(new { id, error = new { code = -32601, message = "Unsupported method" } }); continue;
            }
            await Emit(new { id, result });
            if (method.GetString() == "thread/resume")
            {
                await Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "live-thread", itemId = "external-message", delta = " e continuado" } });
                await Emit(new { id = "foreign-request", method = "item/commandExecution/requestApproval", @params = new { threadId = "other-thread", turnId = "other-turn", command = "foreign" } });
                await Emit(new { id = "approval-live", method = "item/commandExecution/requestApproval", @params = new { threadId = "live-thread", turnId, command = "example" } });
                await Emit(new { method = "turn/completed", @params = new { threadId = "other-thread", turn = new { id = "other-turn", status = "completed" } } });
            }
        }
    }
}
