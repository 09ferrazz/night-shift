using System.Diagnostics;
using NightShift.App;
using NightShift.Core;

internal static class SlimTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Slim arms an idle linked Codex session without sending or consuming a draft", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-slim-idle-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
            try
            {
                view.Platform = CodexPlatform.Desktop;
                await session.ConnectSharedAsync(Server("--shared-server"), new());
                await view.ResumeAsync(new() { ThreadId = "live-thread", ProjectPath = Path.GetTempPath() });
                await Until(() => session.PendingRequests.Count == 1);
                await view.ResolveAsync(session.PendingRequests.Single(), new { decision = "decline" });
                await Until(() => !session.IsBusy && session.PendingRequests.Count == 0);
                Check(session.CanAcceptSharedInput && view.SlimCanSetNightShift, "Idle linked fixture is not capable of monitoring");
                view.Draft = "Preserve this draft for the full application";
                var originalThread = session.ThreadId; var originalTurns = session.TurnsStarted;
                await view.RunAsync(() => view.SetSlimNightShiftAsync(true));
                Check(view.SleepLabel.StartsWith("PC acordado", StringComparison.Ordinal), "Slim did not enable sleep prevention for the armed session");
                Check(session.Shift.Armed, "Idle linked slim enabled a composer flag without arming session monitoring");
                Check(view.NightShiftEnabled && session.ThreadId == originalThread && session.TurnsStarted == originalTurns,
                    "Slim arming sent a task or changed the linked session");
                Check(view.Draft == "Preserve this draft for the full application", "Slim arming consumed the composer draft");
                await view.RunAsync(() => view.SetSlimNightShiftAsync(false));
                Check(!session.Shift.Armed && !view.NightShiftEnabled && session.TurnsStarted == originalTurns, "Slim disarming sent a task");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Slim keeps managed idle Night Shift waiting for the existing composer", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-slim-managed-" + Guid.NewGuid());
            var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(folder), session);
            try
            {
                await session.ConnectAsync(Server("--fake-server"), new());
                await view.ResumeAsync(new() { ThreadId = "managed-slim", ProjectPath = Path.GetTempPath() });
                view.Draft = "Do not send yet";
                await view.RunAsync(() => view.SetSlimNightShiftAsync(true));
                Check(!session.Shift.Armed && view.NightShiftEnabled && session.TurnsStarted == 0 && view.Draft == "Do not send yet",
                    "Managed idle slim changed existing composer semantics or sent a task");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Slim read-only Claude observer cannot enable turn controls", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-slim-observer-" + Guid.NewGuid());
            var store = new AppStore(folder); store.SaveSettings(new() { Provider = AgentProvider.Claude, ClaudePlatform = CodexPlatform.Desktop });
            var session = new ClaudeSession(); await using var view = new MainViewModel(store, session);
            try
            {
                await session.ConnectSharedAsync(Server("--claude-fake-server"), new());
                await session.AttachLiveAsync((await session.ListLiveSessionsAsync()).Single(r => r.ExternalKind == "background"));
                Check(view.HasLiveSession && view.IsReadOnlySession && !view.SlimCanSetNightShift && !view.SlimCanStop && !view.SlimCanContinue,
                    "Claude observation exposed unsupported controls");
                await view.RunAsync(() => view.SetSlimNightShiftAsync(true));
                Check(!session.Shift.Armed && !view.NightShiftEnabled && session.TurnsStarted == 0, "Claude observation began a task or turn");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Slim clamps a removed monitor position into the current work area", () =>
        {
            var position = SlimPlacement.Clamp(4800, -1200, 76, 76, new(0, 0, 1920, 1040));
            Check(position.Left == 1844 && position.Top == 0, "Floating cat can be stranded offscreen");
            return Task.CompletedTask;
        });
        yield return ("Slim expanded panel remains within a negative-coordinate monitor", () =>
        {
            var area = new SlimWorkArea(-1920, 0, 1920, 1040);
            var position = SlimPlacement.Clamp(-40, 1000, 340, 640, area);
            Check(position.Left == -340 && position.Top == 400, "Panel exceeds a monitor work area");
            return Task.CompletedTask;
        });
        yield return ("Slim invalid persisted coordinates use a finite visible default", () =>
        {
            var position = SlimPlacement.Clamp(double.NaN, double.PositiveInfinity, 76, 76, new(0, 0, 800, 600));
            Check(position.Left == 700 && position.Top == 500, "Invalid coordinates were restored");
            var small = SlimPlacement.Clamp(1000, 1000, 340, 640, new(20, 40, 200, 180));
            Check(small.Left == 20 && small.Top == 40, "Oversize panel must align with the reachable work area");
            return Task.CompletedTask;
        });
        yield return ("Slim preserves valid placement inside a monitor", () =>
        {
            var position = SlimPlacement.Clamp(-1300, 75, 76, 76, new(-1920, 0, 1920, 1040));
            Check(position.Left == -1300 && position.Top == 75, "Placement moved unnecessarily");
            return Task.CompletedTask;
        });
        yield return ("Slim placement rejects an invalid work area", () =>
        {
            try { SlimPlacement.Clamp(0, 0, 76, 76, new(0, 0, 0, 600)); }
            catch (ArgumentOutOfRangeException) { return Task.CompletedTask; }
            throw new Exception("Invalid monitor geometry was accepted");
        });
        yield return ("Slim saves finite cat position without replacing session settings", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-slim-" + Guid.NewGuid());
            var store = new AppStore(folder);
            store.SaveSettings(new AppSettings { ShiftHours = 12, ProjectPath = "existing-project" });
            await using var view = new MainViewModel(store);
            try
            {
                Check(!view.SlimCanSetNightShift, "Disconnected slim incorrectly enables a turn");
                view.SaveSlimPosition(-1100, 125);
                var saved = store.LoadSettings();
                Check(saved.SlimLeft == -1100 && saved.SlimTop == 125, "Cat position not persisted");
                Check(saved.ShiftHours == 12 && saved.ProjectPath == "existing-project", "Cat position replaced session settings");
                view.SaveSlimPosition(double.NaN, double.NegativeInfinity);
                saved = store.LoadSettings();
                Check(saved.SlimLeft == -1100 && saved.SlimTop == 125, "Nonfinite cat position overwrote valid placement");
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
    }
    private static ProcessStartInfo Server(string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SlimTests).Assembly.Location);
        start.ArgumentList.Add(argument); return start;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
