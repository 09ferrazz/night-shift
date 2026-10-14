using NightShift.App;
using NightShift.Core;

internal static class ProviderUiTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Provider switching preserves independent drafts attachments and settings without a turn", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ns-provider-ui-" + Guid.NewGuid());
            Directory.CreateDirectory(root); var file = Path.Combine(root, "note.txt"); await File.WriteAllTextAsync(file, "note");
            var store = new AppStore(Path.Combine(root, "data"));
            await using var view = new MainViewModel(store);
            try
            {
                view.Draft = "codex draft"; view.SelectedEffort = "high"; await view.AddAttachmentsAsync([file]);
                var attachment = view.Attachments.Single();
                await view.SwitchProviderAsync(AgentProvider.Claude);
                if (view.Provider != AgentProvider.Claude || view.Draft != "" || view.HasAttachments || view.IsConnected) throw new Exception("Switch did not isolate inactive state");
                view.Draft = "claude draft"; view.SelectedEffort = "medium";
                await view.SwitchProviderAsync(AgentProvider.Codex);
                if (view.Draft != "codex draft" || view.SelectedEffort != "high" || view.Attachments.Single().Id != attachment.Id || !File.Exists(attachment.Path) || view.CurrentThreadId != null)
                    throw new Exception("Switch lost original draft/settings/attachment or started task");
                await view.SwitchProviderAsync(AgentProvider.Claude);
                if (view.Draft != "claude draft" || view.SelectedEffort != "medium") throw new Exception("Claude draft/settings did not survive return");
            }
            finally { await view.DisposeAsync(); Directory.Delete(root, true); }
        });
        yield return ("Claude unsupported commands remain drafts and cached recents stay provider scoped", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ns-provider-cache-" + Guid.NewGuid()); var store = new AppStore(root);
            store.SaveSession(new SessionRecord { ThreadId = "same", Title = "Codex", Provider = AgentProvider.Codex });
            store.SaveSession(new SessionRecord { ThreadId = "same", Title = "Claude", Provider = AgentProvider.Claude });
            await using var view = new MainViewModel(store);
            try
            {
                await view.SwitchProviderAsync(AgentProvider.Claude);
                if (view.RecentSessions.Single().Title != "Claude" || view.Sessions.Any(s => s.Provider != view.Provider)) throw new Exception("Cache leaked another provider");
                foreach (var command in new[] { "/fork", "/review", "/compact", "/mcp", "/init", "/something-unknown" })
                {
                    view.Draft = command;
                    try { await view.SendAsync(); throw new Exception("Unsupported command accepted"); }
                    catch (InvalidOperationException) { }
                    if (view.Draft != command || view.CurrentThreadId != null) throw new Exception("Unsupported command consumed draft or became a prompt");
                }
                if (view.CanExecuteCommand("review") || !view.CanExecuteCommand("model")) throw new Exception("Claude command capability incorrect");
            }
            finally { await view.DisposeAsync(); Directory.Delete(root, true); }
        });
        yield return ("Connected provider switch disposes outgoing adapter reconnects and never sends", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ns-provider-connected-" + Guid.NewGuid()); Directory.CreateDirectory(root);
            var binary = Path.Combine(root, "local-agent.exe"); await File.WriteAllTextAsync(binary, "fixture");
            var outgoing = new ProviderFixture { IsConnected = true, ThreadId = "outgoing" };
            var incoming = new ProviderFixture(); var store = new AppStore(Path.Combine(root, "data"));
            store.SaveSettings(new AppSettings { ClaudePath = binary, CodexPath = binary, ProjectPath = root });
            await using var view = new MainViewModel(store, outgoing, _ => incoming);
            try
            {
                view.Draft = "must not send";
                await view.SwitchProviderAsync(AgentProvider.Claude);
                if (!outgoing.Disposed || incoming.Connects != 1 || incoming.Sends != 0 || !view.IsConnected || view.Provider != AgentProvider.Claude)
                    throw new Exception("Switch missed disposal/reconnection or sent a task");
                if (incoming.Settings?.Provider != AgentProvider.Claude) throw new Exception("Adapter received wrong provider settings");
            }
            finally { await view.DisposeAsync(); Directory.Delete(root, true); }
        });
        yield return ("Switching to websocket-only Codex reconnects shared observer without CLI or send", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ns-provider-websocket-" + Guid.NewGuid());
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", "");
            var store = new AppStore(root);
            store.SaveSettings(new AppSettings { Provider = AgentProvider.Claude, Platform = CodexPlatform.Desktop, SharedEndpoint = "ws://127.0.0.1:5179", CodexPath = "" });
            var outgoing = new ProviderFixture { IsConnected = true }; var incoming = new ProviderFixture();
            await using var view = new MainViewModel(store, outgoing, _ => incoming);
            try
            {
                await view.SwitchProviderAsync(AgentProvider.Codex);
                if (!view.IsConnected || incoming.SharedEndpoint?.AbsoluteUri != "ws://127.0.0.1:5179/" || incoming.Connects != 1 || !incoming.IsShared || incoming.Sends != 0 || !outgoing.Disposed)
                    throw new Exception("Websocket-only switch did not reconnect its saved observer without a CLI");
            }
            finally { await view.DisposeAsync(); Environment.SetEnvironmentVariable("PATH", oldPath); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        yield return ("Failed adapter creation preserves outgoing draft attachments and provider", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ns-provider-factory-" + Guid.NewGuid()); Directory.CreateDirectory(root);
            var file = Path.Combine(root, "keep.txt"); await File.WriteAllTextAsync(file, "keep"); var outgoing = new ProviderFixture();
            await using var view = new MainViewModel(new AppStore(Path.Combine(root, "data")), outgoing, _ => throw new IOException("fixture unavailable"));
            try
            {
                view.Draft = "keep"; await view.AddAttachmentsAsync([file]); var id = view.Attachments.Single().Id;
                await view.SwitchProviderAsync(AgentProvider.Claude);
                if (view.Provider != AgentProvider.Codex || view.Draft != "keep" || view.Attachments.Single().Id != id || outgoing.Disposed || !view.HasNotice)
                    throw new Exception("Failed switch altered outgoing work");
            }
            finally { await view.DisposeAsync(); Directory.Delete(root, true); }
        });
        yield return ("Armed shift blocks provider switch without losing draft", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ns-provider-busy-" + Guid.NewGuid());
            var session = new ProviderFixture(); await using var view = new MainViewModel(new AppStore(root), session);
            try
            {
                view.Draft = "keep"; session.StartShift(2);
                if (view.CanSwitchProvider) throw new Exception("Active shift permits switch");
                await view.SwitchProviderAsync(AgentProvider.Claude);
                if (view.Provider != AgentProvider.Codex || view.Draft != "keep") throw new Exception("Switch interrupted active provider");
            }
            finally { await view.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }
}

internal sealed class ProviderFixture : IAgentSession
{
    public bool Disposed { get; private set; }
    public int Connects { get; private set; }
    public int Sends { get; private set; }
    public AppSettings? Settings { get; private set; }
    public bool IsConnected { get; set; }
    public string? ThreadId { get; set; }
    public bool IsBusy => false;
    public bool IsPreparing => false;
    public bool IsManualOperation => false;
    public bool CanSteer => false;
    public bool IsShared { get; private set; }
    public Uri? SharedEndpoint { get; private set; }
    public bool WaitingInOrigin => false;
    public bool CanAcceptSharedInput => false;
    public bool ContinuationReady => false;
    public int TurnsStarted => Sends;
    public DateTimeOffset? RatesUpdatedAt => null;
    public IReadOnlyList<ChatEntry> RestoredHistory => [];
    public ShiftState Shift { get; } = new();
    public RateSnapshot Rates { get; } = new();
    public string Status => IsConnected ? "Connected fixture" : "Disconnected fixture";
    public IReadOnlyList<PendingRequest> PendingRequests => [];
    public event Action? Changed { add { } remove { } }
    public event Action<ChatEntry>? ChatChanged { add { } remove { } }
    public event Action<string>? Activity { add { } remove { } }
    public event Action<string>? Warning { add { } remove { } }
    public Task ConnectAsync(System.Diagnostics.ProcessStartInfo start, AppSettings settings) { Connects++; IsConnected = true; Settings = settings; return Task.CompletedTask; }
    public Task ConnectSharedAsync(System.Diagnostics.ProcessStartInfo proxy, AppSettings settings) => ConnectAsync(proxy, settings);
    public Task ConnectSharedAsync(Uri endpoint, AppSettings settings) { SharedEndpoint = endpoint; IsShared = true; Connects++; IsConnected = true; Settings = settings; return Task.CompletedTask; }
    private static System.Text.Json.JsonElement Empty => System.Text.Json.JsonSerializer.SerializeToElement(new { });
    public Task<System.Text.Json.JsonElement> AccountAsync() => Task.FromResult(Empty);
    public Task<System.Text.Json.JsonElement> LoginAsync() => Task.FromResult(Empty);
    public Task<System.Text.Json.JsonElement> SetupSandboxAsync() => Task.FromResult(Empty);
    public Task<IReadOnlyList<CodexModel>> ModelsAsync() => Task.FromResult<IReadOnlyList<CodexModel>>([]);
    public void UpdateSettings(AppSettings settings) => Settings = settings;
    public void StartShift(int hours) => Shift.Arm(DateTimeOffset.UtcNow, hours);
    public Task<bool> SendAsync(string project, string text, string model, string effort, IReadOnlyList<Attachment>? attachments = null) { Sends++; return Task.FromResult(true); }
    public Task<System.Text.Json.JsonElement> ResumeAsync(SessionRecord record) => Task.FromResult(Empty);
    public Task NewSessionAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task RefreshQuotaAsync() => Task.CompletedTask;
    public Task CheckRetryAsync(DateTimeOffset now, Func<ContinuationDraft>? draftProvider = null) => Task.CompletedTask;
    public Task ResolveRequestAsync(PendingRequest request, object? result) => Task.CompletedTask;
    public Task<IReadOnlyList<SessionRecord>> ListLiveSessionsAsync(string? project = null) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
    public Task<System.Text.Json.JsonElement> AttachLiveAsync(SessionRecord record) => Task.FromResult(Empty);
    public Task ConfirmSharedContinuationAsync(Func<ContinuationDraft>? draftProvider = null) => Task.CompletedTask;
    public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
    public Task<SessionPage> ListSessionsAsync(string? project = null, string? cursor = null, string? search = null) => Task.FromResult(new SessionPage([], null));
    public Task<SessionRecord?> LatestSessionAsync(string project) => Task.FromResult<SessionRecord?>(null);
    public Task<SessionRecord> ReadSessionAsync(string id) => throw new NotSupportedException();
    public Task ForkAsync() => throw new NotSupportedException();
    public Task ReviewAsync() => throw new NotSupportedException();
    public Task CompactAsync() => throw new NotSupportedException();
    public Task RenameAsync(string name) => throw new NotSupportedException();
    public Task<string> McpStatusAsync() => throw new NotSupportedException();
    public ValueTask DisposeAsync() { Disposed = true; IsConnected = false; return ValueTask.CompletedTask; }
}
