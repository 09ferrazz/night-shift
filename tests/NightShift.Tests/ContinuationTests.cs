using System.Reflection;
using System.Text.Json;
using NightShift.App;
using NightShift.Core;

internal static class ContinuationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Task Tick(MainViewModel view) => (Task)typeof(MainViewModel).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Enabling night shift during quota never sends the draft", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-toggle-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
            try
            {
                await session.ConnectAsync(RegressionTests.Server(), new());
                view.ProjectPath = Path.GetTempPath(); view.Draft = "Keep this until reset";
                session.Shift.WaitForQuota(DateTimeOffset.UtcNow.AddHours(1));
                await view.SetNightShiftAsync(true);
                Check(session.Shift.Armed && session.Shift.WaitingForQuota && session.TurnsStarted == 0 && view.Draft == "Keep this until reset", "The toggle submitted the waiting draft");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        foreach (var scenario in new[] { "accepted", "rejected", "usage", "edited", "stopped", "blocked", "empty", "refresh-edited", "stop-refresh", "pending-refresh", "unknown" })
            yield return ($"Quota continuation draft: {scenario}", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "ns-continue-" + Guid.NewGuid());
                var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
                try
                {
                    await session.ConnectAsync(RegressionTests.Server("draft-" + scenario), new());
                    await session.SendAsync(Path.GetTempPath(), "Initial task", "", "");
                    await session.StopAsync();
                    var due = DateTimeOffset.UtcNow.AddSeconds(-1);
                    session.StartShift(8); session.Shift.WaitForQuota(due);
                    view.Draft = scenario == "empty" ? "  " : "Rascunho do usuário\n/stop é texto nesta mensagem";
                    var original = view.Draft;
                    var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    session.Activity += line => { if (line.Contains("draft-refresh-pending")) refreshing.TrySetResult(); };
                    string? sent = null;
                    session.ChatChanged += entry => { if (entry.Role == "Night Shift") { sent = entry.Text; if (scenario == "edited") { view.Draft = "edit"; view.Draft = original; } } };
                    if (scenario == "stopped") await view.StopAsync();
                    var tick = Tick(view);
                    if (scenario is "refresh-edited" or "stop-refresh" or "pending-refresh")
                    {
                        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        if (scenario == "refresh-edited") view.Draft = original = "Latest draft typed during quota check";
                        if (scenario == "stop-refresh") await view.StopAsync();
                        if (scenario == "pending-refresh")
                        {
                            var pending = (List<PendingRequest>)typeof(CodexSession).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                            pending.Add(new(JsonSerializer.SerializeToElement(99), "item/tool/requestUserInput", JsonSerializer.SerializeToElement(new { })));
                        }
                    }
                    await tick;
                    if (scenario is "stopped" or "blocked" or "stop-refresh" or "pending-refresh" or "unknown")
                        Check(session.TurnsStarted == 1 && sent is null && view.Draft == original, "Blocked/stopped continuation consumed or sent a draft");
                    else
                    {
                        Check(sent is not null && sent.StartsWith(Protocol.ContinueMessage), "Continuation prefix missing");
                        Check(scenario == "empty" ? sent == Protocol.ContinueMessage : sent!.EndsWith(original), "User draft was not appended exactly");
                        var accepted = scenario is "accepted" or "edited" or "empty" or "refresh-edited";
                        Check(session.TurnsStarted == (accepted ? 2 : 1), "Unexpected accepted turn count");
                        Check(view.Draft == (scenario is "accepted" or "refresh-edited" ? "" : original), "Draft cleared after failure or concurrent edit");
                        var count = session.TurnsStarted; await Tick(view);
                        Check(session.TurnsStarted == count, "Continuation was duplicated on the next tick");
                    }
                }
                finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
            });
        yield return ("Quota labels follow server window durations, including reversed and unknown windows", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-rates-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
            try
            {
                Check(!view.HasFiveHourLimit && !view.HasWeeklyLimit && view.FiveHourUsage == "Não informado", "Unknown quota pretends to be empty");
                typeof(CodexSession).GetProperty("Rates")!.SetValue(session, RateSnapshot.Parse(JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = 47, windowDurationMins = 10080 }, secondary = new { usedPercent = 98, windowDurationMins = 300 } } })));
                Check(view.FiveHourUsed == 98 && view.WeeklyUsed == 47 && view.FiveHourReset == "Reset não informado", "Quota labels assume primary/secondary durations");
                typeof(CodexSession).GetProperty("Rates")!.SetValue(session, RateSnapshot.Parse(JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = 20, windowDurationMins = 60 } } })));
                Check(!view.HasFiveHourLimit && !view.HasWeeklyLimit, "Nonstandard duration mislabeled as 5 hours");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
    }
}
