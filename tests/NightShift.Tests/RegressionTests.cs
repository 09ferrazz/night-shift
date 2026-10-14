using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NightShift.App;
using NightShift.Core;

internal static class RegressionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static ProcessStartInfo Server(string scenario = "normal")
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(RegressionTests).Assembly.Location);
        start.ArgumentList.Add("--regression-server"); start.ArgumentList.Add(scenario); return start;
    }
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Primary action sends a staged night task once and its toggle stops it", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-primary-" + Guid.NewGuid());
            var session = new CodexSession(); await session.ConnectAsync(Server(),new());
            await using var view = new MainViewModel(new AppStore(folder),session);
            try
            {
                view.ProjectPath=Path.GetTempPath(); view.Draft="A tarefa só começa ao enviar";
                await view.SetNightShiftAsync(true);
                Check(session.TurnsStarted==0 && view.PrimaryActionLabel=="Enviar" && view.CanPrimaryAction,"Staged shift changed the primary action prematurely");
                await view.SendPrimaryAsync();
                Check(session.TurnsStarted==1 && session.Shift.Armed && view.Draft=="" && view.PrimaryActionLabel=="Enviar" && !view.CanPrimaryAction && view.CanStop,"Primary send lost the shift or duplicated the request");
                await view.SetNightShiftAsync(false);
                Check(!session.IsBusy && !session.Shift.Armed && !view.NightShiftEnabled && view.PrimaryActionLabel=="Enviar","Night toggle failed to stop and reset controls");
                view.Draft="Agora sem turno"; await view.SendPrimaryAsync();
                Check(session.TurnsStarted==2 && !session.Shift.Armed,"Manual send unexpectedly enabled night shift");
                await view.StopAsync();
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder,true); }
        });
        yield return ("Night toggle stages a task without sending or arming a session", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-appearance-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
            try
            {
                view.Draft = "Do not send until Enviar";
                var toggle = typeof(MainViewModel).GetMethod("SetNightShiftAsync");
                Check(toggle is not null, "Separate night-shift toggle is missing");
                await (Task)toggle!.Invoke(view, [true])!;
                Check(!session.IsBusy && !session.Shift.Armed && session.ThreadId is null && view.Draft == "Do not send until Enviar", "Toggle sent or armed a draft before Enviar");
                Check((bool)typeof(MainViewModel).GetProperty("NightShiftEnabled")!.GetValue(view)!, "Staged toggle state was lost");
                await (Task)toggle.Invoke(view, [false])!;
                Check(!(bool)typeof(MainViewModel).GetProperty("NightShiftEnabled")!.GetValue(view)!, "Toggle cannot be disabled");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        foreach (var scenario in new[] { "partial-turns", "partial-items", "partial-summary" })
            yield return ($"Resume hydrates full history for {scenario}", async () =>
            {
                await using var session = new CodexSession(); await session.ConnectAsync(Server(scenario), new());
                await session.ResumeAsync(new() { ThreadId = "good", ProjectPath = Path.GetTempPath() });
                Check(session.RestoredHistory.Count == 2 && session.RestoredHistory[0].Text == "OLD_COMPLETE" && session.RestoredHistory[1].Text == "NEW_COMPLETE", "Partial inline history was treated as complete");
            });
        yield return ("Earlier messages remain accessible and are exported before expanding", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-reg-" + Guid.NewGuid());
            await using var view = new MainViewModel(new AppStore(folder));
            try
            {
                var entries = Enumerable.Range(0, 600).Select(i => new ChatEntry { Id = "id-" + i, Text = "MESSAGE_" + i + "_END" }).ToList();
                typeof(MainViewModel).GetMethod("ReplaceMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [entries]);
                Check(view.Messages.Count == 400 && view.HasEarlierMessages, "Initial visible page is incorrect");
                Check(view.Report().Contains("MESSAGE_0_END"), "Export omitted collapsed history");
                view.LoadEarlierMessages();
                Check(view.Messages.Count == 600 && view.Messages[0].Text == "MESSAGE_0_END" && !view.HasEarlierMessages, "Earlier page not accessible");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        foreach (var length in new[] { 40000, 200000 })
            yield return ($"Full {length}-character response survives stream, history, cache and export", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ns-reg-" + Guid.NewGuid());
                var store = new AppStore(folder); var session = new CodexSession();
                await using var view = new MainViewModel(store, session);
                try
                {
                    var text = new string('á', length) + "\nFINAL_COMPLETO";
                    var history = ThreadHistory.Read(JsonSerializer.SerializeToElement(new { thread = new { turns = new[] { new { items = new[] { new { id = "long", type = "agentMessage", text } } } } } }));
                    Check(history.Single().Text == text, "History truncated the response");
                    typeof(CodexSession).GetMethod("UpdateChat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, ["long", text[..20000], true]);
                    typeof(CodexSession).GetMethod("UpdateChat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, ["long", text[20000..], true]);
                    Check(view.Report().Contains(text), "Stream/export lost response content");
                    store.SaveSession(new() { ThreadId = "long", Messages = history });
                    Check(store.LoadSession("long")!.Messages.Single().Text == text, "Cache truncated the response");
                }
                finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
            });
        yield return ("Stop is usable while an auxiliary request is pending", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-reg-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await session.ConnectAsync(Server(), new());
                await session.SendAsync(Path.GetTempPath(), "test", "", "");
                var pending = view.RunAsync(() => release.Task);
                try
                {
                    Check(view.CanStop, "Stop disabled during auxiliary operation");
                    Check(view.CanExecuteCommand("stop"), "/stop disabled during auxiliary operation");
                    await view.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    Check(!session.IsBusy && !pending.IsCompleted, "Stop waited for the auxiliary operation");
                }
                finally { release.SetResult(); await pending; }
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Stop during thread creation never launches a late turn", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectAsync(Server("slow-thread"), new());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Activity += line => { if (line.Contains("thread-start-pending")) entered.TrySetResult(); };
            var send = session.SendAsync(Path.GetTempPath(), "test", "", "");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await session.StopAsync(); await send;
            Check(session.TurnsStarted == 0 && !session.IsBusy, "A turn started after Stop during thread creation");
        });
        yield return ("Live catalog skips only unavailable threads and keeps its connection", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server("missing-thread"), new());
            var warnings = new List<string>(); session.Warning += warnings.Add;
            var list = await session.ListLiveSessionsAsync();
            Check(list.Count == 1 && list[0].ThreadId == "good", "Healthy thread missing from partial catalog");
            Check(session.IsConnected && warnings.Count > 0, "Missing thread was hidden or disconnected the client");
        });
        yield return ("Live catalog exposes permission errors", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server("denied-thread"), new());
            try { await session.ListLiveSessionsAsync(); }
            catch (RpcException ex) { Check(ex.Message == "Permission denied", "Unexpected error"); return; }
            throw new Exception("Permission error was silently skipped");
        });
        yield return ("Live catalog skips an empty rollout without hiding other internal failures", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server("empty-rollout"), new());
            Check((await session.ListLiveSessionsAsync()).Count == 1, "An empty rollout aborted the catalog");
            await session.DisconnectAsync(); await session.ConnectSharedAsync(Server("corrupt-rollout"), new());
            try { await session.ListLiveSessionsAsync(); }
            catch (RpcException ex) { Check(ex.Code == -32603, "Unexpected internal error"); return; }
            throw new Exception("A corrupt rollout was silently skipped");
        });
        yield return ("Stop preserves a newer external turn started during interruption", async () =>
        {
            await using var session = new CodexSession(); await session.ConnectSharedAsync(Server("external-during-stop"), new());
            await session.AttachLiveAsync(new() { ThreadId = "good", ProjectPath = Path.GetTempPath() });
            Check(session.IsBusy, "Expected an active external turn");
            await session.StopAsync();
            Check(session.IsBusy, "Stop cleared or interrupted the newer external turn");
        });
    }
    public static async Task RunServerAsync(string scenario)
    {
        int turnStarts = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line); var m = document.RootElement;
            if (!m.TryGetProperty("id", out var id) || !m.TryGetProperty("method", out var method)) continue;
            var p = m.GetProperty("params"); object result = new { };
            switch (method.GetString())
            {
                case "thread/start":
                    if (scenario == "slow-thread") { await Console.Error.WriteLineAsync("thread-start-pending"); await Console.Error.FlushAsync(); await Task.Delay(350); }
                    result = new { thread = new { id = "good" } }; break;
                case "turn/start":
                    if (scenario is "attachment-slow-accept" or "attachment-slow-reject")
                    { await Console.Error.WriteLineAsync("attachment-turn-pending"); await Console.Error.FlushAsync(); await Task.Delay(350); }
                    if (scenario == "attachment-slow-reject")
                    { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, error = new { code = -32000, message = "Rejected" } })); await Console.Out.FlushAsync(); continue; }
                    if (++turnStarts > 1 && scenario is "draft-rejected" or "draft-usage")
                    {
                        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, error = new { code = -32000, message = "Rejected", data = new { codexErrorInfo = scenario == "draft-usage" ? "usageLimitExceeded" : "other" } } }));
                        await Console.Out.FlushAsync(); continue;
                    }
                    result = new { turn = new { id = "turn-1" } }; break;
                case "account/rateLimits/read":
                    if (scenario is "draft-refresh-edited" or "draft-stop-refresh" or "draft-pending-refresh")
                    { await Console.Error.WriteLineAsync("draft-refresh-pending"); await Console.Error.FlushAsync(); await Task.Delay(350); }
                    if (scenario == "draft-unknown") break;
                    result = new { rateLimits = new { primary = new { usedPercent = scenario == "draft-blocked" ? 100 : 20, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } } }; break;
                case "thread/resume":
                    result = scenario.StartsWith("partial-") ? new
                    {
                        thread = new { id = "good", cwd = Path.GetTempPath(), turns = new[] { new { id = "recent", status = "completed", itemsView = scenario == "partial-summary" ? "summary" : "full", items = new[] { new { id = "new", type = "agentMessage", text = "INLINE_SUMMARY" } } } } },
                        turnsBackwardsCursor = scenario == "partial-turns" ? "older-turns" : null,
                        itemsBackwardsCursor = scenario == "partial-items" ? "older-items" : null
                    } : (object)new { thread = new { id = "good", cwd = Path.GetTempPath(), status = new { type = "active" }, turns = new[] { new { id = "turn-1", status = "inProgress", items = Array.Empty<object>() } } } }; break;
                case "thread/turns/list":
                    result = new { data = scenario.StartsWith("partial-") ? new[]
                    {
                        new { id = "recent", itemsView = "full", items = new[] { new { id = "new", type = "agentMessage", text = "NEW_COMPLETE" } } },
                        new { id = "older", itemsView = "full", items = new[] { new { id = "old", type = "agentMessage", text = "OLD_COMPLETE" } } }
                    } : [], nextCursor = (string?)null }; break;
                case "turn/interrupt":
                    if (scenario == "external-during-stop")
                    {
                        Check(p.GetProperty("turnId").GetString() == "turn-1", "Must not interrupt the newer external turn");
                        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { method = "turn/started", @params = new { threadId = "good", turn = new { id = "external-new" } } }));
                        await Console.Out.FlushAsync(); await Task.Delay(100);
                    }
                    break;
                case "thread/loaded/list": result = new { data = new[] { "missing", "good" }, nextCursor = (string?)null }; break;
                case "thread/read":
                    if (p.GetProperty("threadId").GetString() == "missing")
                    {
                        var error = scenario switch
                        {
                            "denied-thread" => (-32600, "Permission denied"),
                            "empty-rollout" => (-32603, "failed to read thread: thread-store internal error: failed to read thread test.jsonl: rollout at test.jsonl is empty"),
                            "corrupt-rollout" => (-32603, "failed to read thread: thread-store internal error: invalid JSON"),
                            _ => (-32600, "no rollout found for thread id missing")
                        };
                        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, error = new { code = error.Item1, message = error.Item2 } }));
                        await Console.Out.FlushAsync(); continue;
                    }
                    result = new { thread = new { id = "good", cwd = Path.GetTempPath(), status = new { type = "idle" } } }; break;
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, result })); await Console.Out.FlushAsync();
        }
    }
}
