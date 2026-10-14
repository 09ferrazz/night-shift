using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NightShift.Core;

internal static class RealProtocolProbe
{
    public static async Task<int> RunAsync(string executable, string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        // An empty Codex home prevents touching the user's sessions/configuration or spending model quota.
        var home = Path.Combine(directory, "codex-home"); Directory.CreateDirectory(home);
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var endpoint = new Uri($"ws://127.0.0.1:{port}");
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add(endpoint.AbsoluteUri.TrimEnd('/'));
        start.Environment["CODEX_HOME"] = home;
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            JsonRpcClient? actor = null;
            for (int attempt = 0; attempt < 30 && actor is null; attempt++)
            {
                if (process.HasExited) throw new IOException("Server exited: " + await stderr);
                try { actor = await JsonRpcClient.ConnectWebSocketAsync(endpoint); }
                catch (Exception) when (!process.HasExited && attempt < 29) { await Task.Delay(200); }
            }
            await using var owner = actor ?? throw new IOException("No WebSocket connection");
            await owner.RequestAsync("initialize", new { clientInfo = new { name = "night_shift_probe", version = "0.2.0" }, capabilities = new { experimentalApi = true } });
            await owner.NotifyAsync("initialized", new { }); Console.WriteLine("PASS REAL WebSocket initialize (installed Codex)");
            var account = await owner.RequestAsync("account/read", new { refreshToken = false });
            if (account.GetProperty("account").ValueKind != JsonValueKind.Null) throw new Exception("Probe must use an unauthenticated isolated Codex home");
            var created = await owner.RequestAsync("thread/start", new { cwd = directory, approvalPolicy = "on-request", sandbox = "read-only" }, TimeSpan.FromSeconds(30));
            var id = created.GetProperty("thread").GetProperty("id").GetString()!;
            Console.WriteLine("PASS REAL thread/start without model inference");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.MessageReceived += message => { if (CodexSession.Text(message, "method") == "turn/completed") completed.TrySetResult(); };
            await owner.RequestAsync("turn/start", new { threadId = id, input = new[] { new { type = "text", text = "Validation only. Reply OK without tools." } } });
            Console.WriteLine("PASS REAL turn accepted in unauthenticated isolated home; interrupt before completion");
            await using var observer = new CodexSession();
            await observer.ConnectSharedAsync(endpoint, new());
            var live = await observer.ListLiveSessionsAsync(directory);
            var record = live.Single(r => r.ThreadId == id);
            await observer.AttachLiveAsync(record);
            Console.WriteLine("PASS REAL two clients share loaded session and subscribe through thread/resume");
            await observer.StopAsync();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("PASS REAL observer interrupts only the selected active turn");
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.Changed += () => { if (observer.IsBusy) observed.TrySetResult(); };
            await owner.RequestAsync("turn/start", new { threadId = id, input = new[] { new { type = "text", text = "Validation only. Reply OK without tools." } } });
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("PASS REAL observer receives another client's turn/started event");
            await observer.StopAsync();
            await observer.DisconnectAsync();
            var loaded = await owner.RequestAsync("thread/loaded/list", new { });
            if (!loaded.GetProperty("data").EnumerateArray().Any(x => x.GetString() == id)) throw new Exception("Disconnect unloaded the owner session");
            Console.WriteLine("PASS REAL observer disconnect preserves backend and owner's loaded session");
            Console.WriteLine("NOT TESTED: Desktop/VS Code applications, model completion, real quota exhaustion, real permission prompt");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL REAL " + ex); return 1; }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(); await Task.WhenAll(stderr, stdout);
        }
    }
}
