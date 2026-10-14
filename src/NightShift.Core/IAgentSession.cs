using System.Diagnostics;
using System.Text.Json;
namespace NightShift.Core;

public enum AgentProvider { Codex = 0, Claude = 1 }

/// <summary>Provider adapters preserve native policies and expose only supported session capabilities.</summary>
public interface IAgentSession : IAsyncDisposable
{
    bool IsManualOperation { get; }
    DateTimeOffset? RatesUpdatedAt { get; }
    string? ThreadId { get; }
    bool IsConnected { get; }
    bool IsBusy { get; }
    bool CanSteer { get; }
    bool IsPreparing { get; }
    int TurnsStarted { get; }
    IReadOnlyList<ChatEntry> RestoredHistory { get; }
    ShiftState Shift { get; }
    RateSnapshot Rates { get; }
    string Status { get; }
    IReadOnlyList<PendingRequest> PendingRequests { get; }
    bool IsShared { get; }
    bool WaitingInOrigin { get; }
    bool CanAcceptSharedInput { get; }
    bool ContinuationReady { get; }
    bool IsReadOnly => false;
    bool CanOpenOrigin => false;
    Task OpenOriginAsync() => throw new NotSupportedException("Esta origem não oferece abertura pelo conector.");
    event Action? Changed;
    event Action<ChatEntry>? ChatChanged;
    event Action<string>? Activity;
    event Action<string>? Warning;
    Task ConnectAsync(ProcessStartInfo start, AppSettings settings);
    Task ConnectSharedAsync(ProcessStartInfo proxy, AppSettings settings);
    Task ConnectSharedAsync(Uri endpoint, AppSettings settings);
    Task<JsonElement> AccountAsync();
    Task<JsonElement> LoginAsync();
    Task<JsonElement> SetupSandboxAsync();
    Task<IReadOnlyList<CodexModel>> ModelsAsync();
    void UpdateSettings(AppSettings settings);
    void StartShift(int hours);
    Task<bool> SendAsync(string project, string text, string model, string effort, IReadOnlyList<Attachment>? attachments = null);
    Task<JsonElement> ResumeAsync(SessionRecord record);
    Task NewSessionAsync();
    Task StopAsync();
    Task RefreshQuotaAsync();
    Task CheckRetryAsync(DateTimeOffset now, Func<ContinuationDraft>? draftProvider = null);
    Task ResolveRequestAsync(PendingRequest request, object? result);
    Task<IReadOnlyList<SessionRecord>> ListLiveSessionsAsync(string? project = null);
    Task<JsonElement> AttachLiveAsync(SessionRecord record);
    Task ConfirmSharedContinuationAsync(Func<ContinuationDraft>? draftProvider = null);
    Task DisconnectAsync();
    Task<SessionPage> ListSessionsAsync(string? project = null, string? cursor = null, string? search = null);
    Task<SessionRecord?> LatestSessionAsync(string project);
    Task<SessionRecord> ReadSessionAsync(string id);
    Task ForkAsync();
    Task ReviewAsync();
    Task CompactAsync();
    Task RenameAsync(string name);
    Task<string> McpStatusAsync();
}
