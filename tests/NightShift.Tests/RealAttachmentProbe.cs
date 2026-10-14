using NightShift.Core;

internal static class RealAttachmentProbe
{
    public static async Task<int> RunAsync(string directory)
    {
        directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
        var start=CodexLocator.StartInfo(CodexLocator.Find(null) ?? throw new Exception("Codex not installed"),false);
        var home=Path.Combine(directory,"codex-home");Directory.CreateDirectory(home);start.Environment["CODEX_HOME"]=home;
        start.WorkingDirectory=directory;
        try
        {
            var image=Path.Combine(directory,"pixel.png");
            await File.WriteAllBytesAsync(image,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0foAAAAASUVORK5CYII="));
            var file=Path.Combine(directory,"reference.txt");await File.WriteAllTextAsync(file,"Local attachment validation fixture.");
            var store=new AttachmentStore(Path.Combine(directory,"data"));
            var attachments=new[] {await store.ImportAsync(image),await store.ImportAsync(file)};
            await using var session=new CodexSession();
            await session.ConnectAsync(start,new());
            var account=await session.AccountAsync();
            if(account.TryGetProperty("account",out var details) && details.ValueKind!=System.Text.Json.JsonValueKind.Null)
                throw new Exception("Probe must be unauthenticated and isolated.");
            var accepted=await session.SendAsync(directory,"Protocol validation only. Do not execute tools.","","",attachments);
            if(!accepted || session.TurnsStarted!=1)throw new Exception("Image/file input was not accepted.");
            try { await session.StopAsync(); }
            catch(RpcException ex) when(ex.Message=="no active turn to interrupt")
            { Console.WriteLine("INFO unauthenticated turn already ended before interrupt."); }
            Console.WriteLine("PASS REAL Codex 0.158.0 stdio: turn/start accepted native localImage + local file manifest; isolated home without credentials; own process disposed.");
            Console.WriteLine("NOT TESTED: model image understanding/document parsing, authenticated inference, Desktop/VS Code applications.");
            return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL REAL ATTACHMENTS "+ex);return 1;}
    }
}
