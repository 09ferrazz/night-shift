using System.Diagnostics;

namespace NightShift.Core;

public static class CodexLocator
{
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var names = OperatingSystem.IsWindows() ? new[] { "codex.exe", "codex.cmd" } : new[] { "codex" };
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var dir in directories)
        foreach (var name in names)
        {
            var path = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    public static ProcessStartInfo StartInfo(string path, bool autoReview)
    {
        var parameters = new[] { "app-server", "--listen", "stdio://", "-c", $"approvals_reviewer=\"{(autoReview ? "auto_review" : "user")}\"" };
        return CommandInfo(path, parameters);
    }
    public static ProcessStartInfo ProxyInfo(string path, string socket)
    {
        if (!Path.IsPathFullyQualified(socket)) throw new ArgumentException("O caminho do socket precisa ser absoluto.");
        return CommandInfo(path, ["app-server", "proxy", "--sock", socket]);
    }
    private static ProcessStartInfo CommandInfo(string path, string[] parameters)
    {
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer npm's real JavaScript entry: cmd.exe quoting is avoided entirely.
            var entry = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (!File.Exists(entry)) throw new IOException("Selecione codex.exe ou o codex.cmd instalado pelo npm oficial.");
            var node = FindNode();
            var start = new ProcessStartInfo(node);
            start.ArgumentList.Add(entry);
            foreach (var arg in parameters) start.ArgumentList.Add(arg);
            return start;
        }
        var info = new ProcessStartInfo(path);
        foreach (var arg in parameters) info.ArgumentList.Add(arg);
        return info;
    }
    private static string FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var node = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(node)) return node;
        }
        throw new IOException("O Codex instalado pelo npm precisa do Node.js no PATH. Selecione codex.exe se você usa a instalação nativa.");
    }
}
