using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace NightShift.Core;

public sealed class AppStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly long _retentionBytes;
    public string Root => _root;
    public AppStore(string? root = null, long retentionBytes = 256 * 1024 * 1024)
    {
        _root = root ?? DefaultRoot;
        _retentionBytes = Math.Max(16384, retentionBytes);
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
    }
    public static string DefaultRoot => File.Exists(Path.Combine(AppContext.BaseDirectory, "nightshift.portable"))
        ? Path.Combine(AppContext.BaseDirectory, "Dados")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightShift-v0.6");
    public static string InstanceName => "Local\\NightShift." + Hash(Path.GetFullPath(DefaultRoot).ToUpperInvariant())[..24];
    public AppSettings LoadSettings()
    {
        lock (_gate)
        {
            var settings = Read<AppSettings>(Path.Combine(_root, "settings.json")) ?? new();
            settings.ShiftHours = Math.Clamp(settings.ShiftHours, 1, 24);
            return settings;
        }
    }
    public void SaveSettings(AppSettings settings) { lock (_gate) Write(Path.Combine(_root, "settings.json"), settings); }
    public void SaveSession(SessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ThreadId)) return;
        lock (_gate)
        {
            // Retention removes old sessions as a whole; it must not destroy parts
            // of the current conversation. The current session may exceed the budget.
            var hash = SessionHash(record.ThreadId, record.Provider);
            var folder = Path.Combine(_root, "sessions");
            Write(Path.Combine(folder, hash + ".json"), record);
            Write(Path.Combine(folder, hash + ".meta.json"), new SessionRecord { ThreadId = record.ThreadId,
                Provider = record.Provider, ExternalId = record.ExternalId, ExternalKind = record.ExternalKind,
                ProjectPath = record.ProjectPath, Title = record.Title, Source = record.Source, UpdatedAt = record.UpdatedAt });
            TrimRetention(hash);
        }
    }
    public List<SessionRecord> LoadSessions()
    {
        lock (_gate) return Directory.EnumerateFiles(Path.Combine(_root, "sessions"), "*.meta.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(100)
            .Select(p => Read<SessionRecord>(p, 64 * 1024)).OfType<SessionRecord>().Where(r => !string.IsNullOrWhiteSpace(r.ThreadId))
            .OrderByDescending(r => r.UpdatedAt).Take(100).ToList();
    }
    public SessionRecord? LoadSession(string threadId, AgentProvider provider = AgentProvider.Codex)
    {
        lock (_gate) return Read<SessionRecord>(Path.Combine(_root, "sessions", SessionHash(threadId, provider) + ".json"), long.MaxValue);
    }
    private static string SessionHash(string id, AgentProvider provider) => provider == AgentProvider.Codex ? Hash(id) : "CLAUDE-" + Hash(id);
    private static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private void TrimRetention(string current)
    {
        var folder = Path.Combine(_root, "sessions");
        var files = Directory.GetFiles(folder);
        long total = files.Sum(f => new FileInfo(f).Length);
        var summaries = files.Where(f => f.EndsWith(".meta.json", StringComparison.Ordinal))
            .OrderBy(File.GetLastWriteTimeUtc).ToList();
        int count = summaries.Count;
        foreach (var meta in summaries)
        {
            if (count <= 100 && total <= _retentionBytes) break;
            var stem = Path.GetFileName(meta)[..^10];
            if (stem == current) continue;
            foreach (var file in files.Where(f => Path.GetFileName(f).StartsWith(stem + ".", StringComparison.Ordinal)))
            {
                total -= new FileInfo(file).Length; File.Delete(file);
            }
            count--;
        }
    }
    private T? Read<T>(string path, long maxBytes = 16 * 1024 * 1024)
    {
        if (!File.Exists(path)) return default;
        try
        {
            if (new FileInfo(path).Length > maxBytes) throw new JsonException("Arquivo grande demais.");
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); } catch (IOException) { }
            if (File.Exists(path + ".bak"))
            {
                try
                {
                    if (new FileInfo(path + ".bak").Length > maxBytes) return default;
                    T? restored;
                    using (var stream = File.OpenRead(path + ".bak")) restored = JsonSerializer.Deserialize<T>(stream, Options);
                    if (restored is not null) Write(path, restored);
                    return restored;
                }
                catch (Exception backupError) when (backupError is JsonException or IOException) { }
            }
            return default;
        }
    }
    private static void Write<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, value, Options);
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
