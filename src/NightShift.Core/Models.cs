namespace NightShift.Core;

public sealed class AppSettings
{
    public AgentProvider Provider { get; set; }
    public string ClaudePath { get; set; } = "";
    public string ClaudeModel { get; set; } = "";
    public string ClaudeEffort { get; set; } = "";
    public CodexPlatform ClaudePlatform { get; set; }
    public double? SlimLeft { get; set; }
    public double? SlimTop { get; set; }
    public bool DarkTheme { get; set; } = true;
    public CodexPlatform Platform { get; set; }
    public string SharedEndpoint { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string CodexPath { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public bool AutoReview { get; set; } = true;
    public bool PreventSleep { get; set; } = true;
    public int ShiftHours { get; set; } = 8;
}

public sealed class ChatEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "Codex";
    public string Text { get; set; } = "";
    public string Kind { get; set; } = "message";
    public List<Attachment> Attachments { get; set; } = [];
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
}

public sealed class SessionRecord
{
    public AgentProvider Provider { get; set; }
    public string ExternalId { get; set; } = "";
    public string ExternalKind { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string Title { get; set; } = "Nova sessão";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<ChatEntry> Messages { get; set; } = [];
    public string Source { get; set; } = "night_shift";
    public string Originator { get; set; } = "";
    public string RuntimeStatus { get; set; } = "";
    public string LiveLabel => RuntimeStatus == "active" ? "Em andamento" : RuntimeStatus == "idle" ? "Aguardando mensagem" : RuntimeStatus;
    public string SourceLabel => Provider == AgentProvider.Claude ? (ExternalKind == "background" ? "Claude background" : "Claude Code") : Source switch { "cli" => "CLI", "vscode" => "VS Code", "appServer" => "App Server", "night_shift" => "Night Shift", _ => "Codex" };
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Nova sessão" : Title;
    public string DisplayDate => UpdatedAt.ToLocalTime().ToString("dd/MM · HH:mm");
}

public sealed record CodexModel(string Id, string Name, string DefaultEffort, string[] Efforts)
{
    public override string ToString() => Name;
}
