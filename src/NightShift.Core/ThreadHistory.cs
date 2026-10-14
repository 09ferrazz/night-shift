using System.Text.Json;
namespace NightShift.Core;

public static class ThreadHistory
{
    public static List<ChatEntry> Read(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread) || !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array) return [];
        return ReadTurns(turns);
    }
    public static List<ChatEntry> ReadTurns(JsonElement turns)
    {
        var entries = new Queue<ChatEntry>();
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                var type = CodexSession.Text(item, "type");
                string? text = null; var role = "Codex";
                if (type == "agentMessage") text = CodexSession.Text(item, "text");
                else if (type == "userMessage")
                {
                    role = "Você";
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        text = string.Join("\n", content.EnumerateArray().Select(c => CodexSession.Text(c, "text")).OfType<string>());
                    if (text?.StartsWith(Protocol.Preamble, StringComparison.Ordinal) == true)
                    {
                        var marker = text.IndexOf("Tarefa do usuário:\n", StringComparison.Ordinal);
                        if (marker >= 0) text = text[(marker + "Tarefa do usuário:\n".Length)..];
                    }
                }
                (string Text, List<Attachment> Attachments) parsed = type == "userMessage" ? AttachmentInput.Read(text ?? "") : (text ?? "", []);
                var attachments = parsed.Attachments;
                if (type == "userMessage" && attachments.Count == 0 && item.TryGetProperty("content", out var nativeContent) && nativeContent.ValueKind == JsonValueKind.Array)
                    foreach (var input in nativeContent.EnumerateArray())
                        if (CodexSession.Text(input, "type") == "localImage" && CodexSession.Text(input, "path") is { } path)
                            attachments.Add(new(Guid.NewGuid().ToString("N"), Path.GetFileName(path), path, true, 0));
                if (string.IsNullOrEmpty(parsed.Text) && attachments.Count == 0) continue;
                entries.Enqueue(new() { Id = CodexSession.Text(item, "id") ?? Guid.NewGuid().ToString("N"), Role = role,
                    Text = parsed.Text, Attachments = attachments, Time = DateTimeOffset.MinValue });
            }
        }
        return entries.ToList();
    }
}
