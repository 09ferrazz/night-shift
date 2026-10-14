using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightShift.Core;

internal static class WebSocketTests
{
    public static async Task FragmentationAndDisconnectAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = tcp.GetStream(); var header = new List<byte>(); var one = new byte[1];
            while (await stream.ReadAsync(one, timeout.Token) != 0)
            {
                header.Add(one[0]);
                if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            var key = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").Single(s => s.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
            using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
            var buffer = new byte[8192]; var request = await ws.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            using var parsed = JsonDocument.Parse(buffer.AsMemory(0, request.Count));
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = parsed.RootElement.GetProperty("id"), result = new { message = "ação 🌙" } }));
            for (int offset = 0; offset < bytes.Length; offset += 3)
                await ws.SendAsync(bytes.AsMemory(offset, Math.Min(3, bytes.Length - offset)), WebSocketMessageType.Text, offset + 3 >= bytes.Length, timeout.Token);
            await ws.ReceiveAsync(buffer.AsMemory(), timeout.Token); // deliberately close with a request pending
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
        });
        await using var rpc = await JsonRpcClient.ConnectWebSocketAsync(new Uri($"ws://127.0.0.1:{port}"));
        var result = await rpc.RequestAsync("echo", new { text = "test" });
        if (result.GetProperty("message").GetString() != "ação 🌙") throw new Exception("Fragmented Unicode corrupted");
        try { await rpc.RequestAsync("pending", new { }, TimeSpan.FromSeconds(5)); throw new Exception("EOF did not fail pending request"); }
        catch (IOException) { }
        if (rpc.IsAlive) throw new Exception("Closed socket still reported connected");
        await server;
    }
}
