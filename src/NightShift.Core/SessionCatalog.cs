using System.Text.Json;
namespace NightShift.Core;

public sealed record SessionPage(IReadOnlyList<SessionRecord> Sessions, string? NextCursor);

public static class SessionCatalog
{
    public static bool SameProject(string left, string right)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    public static SessionRecord? ReadThread(JsonElement thread)
    {
        var id = CodexSession.Text(thread, "id"); var cwd = CodexSession.Text(thread, "cwd");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return null;
        var title = CodexSession.Text(thread, "name") ?? CodexSession.Text(thread, "preview") ?? "Conversa Codex";
        var updated = DateTimeOffset.MinValue;
        if (thread.TryGetProperty("updatedAt", out var time) && time.TryGetInt64(out var seconds))
            try { updated = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        return new() { ThreadId = id, ProjectPath = cwd, Title = title.Length > 160 ? title[..160] + "…" : title,
            UpdatedAt = updated, Source = CodexSession.Text(thread, "source") ?? "codex" };
    }
    public static SessionPage Read(JsonElement result, string? project = null)
    {
        var records = new List<SessionRecord>();
        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var thread in data.EnumerateArray().Take(100))
                if (ReadThread(thread) is { } record && (project is null || SameProject(record.ProjectPath, project))) records.Add(record);
        return new(records.DistinctBy(r => r.ThreadId).ToArray(), CodexSession.Text(result, "nextCursor"));
    }
}
