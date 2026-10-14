using System.Diagnostics;
using NightShift.Core;

internal static class RealSocketProbe
{
    public static async Task<int> RunAsync(string executable)
    {
        var home = Path.Combine(Path.GetTempPath(), "ns-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var socket = Path.Combine(home, "app-server-control", "app-server-control.sock");
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["CODEX_HOME"] = home;
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("unix://");
        using var backend = Process.Start(start)!;
        var error = backend.StandardError.ReadToEndAsync(); var output = backend.StandardOutput.ReadToEndAsync();
        try
        {
            for (var n = 0; n < 50 && !File.Exists(socket) && !backend.HasExited; n++) await Task.Delay(100);
            if (backend.HasExited) throw new IOException(await error);
            if (!File.Exists(socket)) throw new IOException("Backend did not create the expected socket: " + socket);
            var proxy = CodexLocator.ProxyInfo(executable, socket); proxy.Environment["CODEX_HOME"] = home;
            await using (var session = new CodexSession())
            {
                session.Activity += Console.WriteLine;
                await session.ConnectSharedAsync(proxy, new());
                await session.ListLiveSessionsAsync();
                await session.DisconnectAsync();
            }
            if (backend.HasExited) throw new IOException("Disconnect killed the shared backend");
            Console.WriteLine("PASS REAL Windows Unix socket + official stdio proxy initialize/list/disconnect; backend survives");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL REAL SOCKET " + ex); return 1; }
        finally
        {
            if (!backend.HasExited) backend.Kill(true);
            await backend.WaitForExitAsync(); await Task.WhenAll(error, output);
            Console.WriteLine("BACKEND STDERR " + await error);
            Console.WriteLine("BACKEND STDOUT " + await output);
        }
    }
}
