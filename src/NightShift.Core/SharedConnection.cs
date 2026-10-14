namespace NightShift.Core;

public enum CodexPlatform { Cli, Desktop, VsCode }

/// <summary>Explicit connections to an existing backend. No implicit CLI fallback.</summary>
public static class SharedConnection
{
    public static string DefaultSocket => Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "app-server-control", "app-server-control.sock");

    public static Uri ParseWebSocket(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "ws" ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host is "127.0.0.1" or "[::1]" or "::1") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Use um WebSocket local, como ws://127.0.0.1:4500, sem senha ou parâmetros no endereço.");
        return uri;
    }
}
