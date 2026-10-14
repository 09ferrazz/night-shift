using System.Reflection;
using System.Text.Json;
using NightShift.Core;
using NightShift.App;

internal static class AttachmentTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Attachment snapshot is immutable, bounded and rejects directories", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-attachment-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "notes.txt"); await File.WriteAllTextAsync(path, "original");
                var store = new AttachmentStore(Path.Combine(folder, "data"));
                var item = await store.ImportAsync(path);
                await File.WriteAllTextAsync(path, "changed");
                Check(await File.ReadAllTextAsync(item.Path) == "original" && item.Name == "notes.txt" && !item.IsImage, "Attachment was not a stable copy");
                try { await store.ImportAsync(folder); throw new Exception("Directory accepted"); } catch (FileNotFoundException) { }
                var large = Path.Combine(folder, "large.dat"); using (var file = File.Create(large)) file.SetLength(AttachmentStore.MaxFileBytes + 1);
                try { await store.ImportAsync(large); throw new Exception("Oversized attachment accepted"); } catch (InvalidOperationException) { }
            }
            finally { Directory.Delete(folder, true); }
        });
        yield return ("Native images and local file references survive canonical history and cache", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-input-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
            try
            {
                var sourceFile=Path.Combine(folder,"notes.txt"); await File.WriteAllTextAsync(sourceFile,"note");
                var sourceImage=Path.Combine(folder,"screen.png"); await File.WriteAllBytesAsync(sourceImage,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0foAAAAASUVORK5CYII="));
                var store=new AttachmentStore(Path.Combine(folder,"data"));
                var file=await store.ImportAsync(sourceFile);
                var picture=await store.ImportAsync(sourceImage);
                var input = AttachmentInput.Build("Read these", [file,picture]);
                var json = JsonSerializer.SerializeToElement(input);
                Check(json[1].GetProperty("type").GetString() == "localImage" && json[1].GetProperty("path").GetString() == picture.Path, "Image was not native input");
                Check(json[0].GetProperty("text").GetString()!.Contains(file.Path.Replace("\\", "\\\\")), "File path missing from prompt");
                var parsed = ThreadHistory.ReadTurns(JsonSerializer.SerializeToElement(new[] {new {items = new[] {new {id="msg",type="userMessage",content=input}}}})).Single();
                Check(parsed.Text == "Read these" && parsed.Attachments.Count == 2 && parsed.Attachments[0].Name == "notes.txt", "History lost attachments or exposed manifest");
                var cache = new AppStore(Path.Combine(folder,"cache")); cache.SaveSession(new() {ThreadId="thread",Messages=[parsed]});
                Check(cache.LoadSession("thread")!.Messages[0].Attachments.Count == 2,"Cache lost attachment metadata");
            }
            finally { Directory.Delete(folder,true); }
            await Task.CompletedTask;
        });
        foreach (var scenario in new[] { "accepted", "rejected", "usage" })
            yield return ($"Attachments send and retention: {scenario}", async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(),"ns-send-"+Guid.NewGuid()); Directory.CreateDirectory(folder);
                var session = new CodexSession(); await using var view = new MainViewModel(new AppStore(Path.Combine(folder,"data")), session);
                try
                {
                    var path=Path.Combine(folder,"sample.txt"); await File.WriteAllTextAsync(path,"sample");
                    await session.ConnectAsync(RegressionTests.Server("draft-"+scenario),new());
                    await session.SendAsync(folder,"initial","",""); await session.StopAsync();
                    view.ProjectPath=folder; view.Draft="See attachment"; await view.AddAttachmentsAsync([path]);
                    try { await view.SendAsync(); } catch (RpcException) when(scenario=="rejected") { }
                    Check(view.Attachments.Count==(scenario=="accepted"?0:1),"Attachments cleared without acceptance");
                    Check(view.Draft==(scenario=="accepted"?"":"See attachment"),"Draft retention differs from attachments");
                    if (scenario == "usage") Check(session.Shift.WaitingForQuota,"Structured RPC usage limit was not recognized");
                    Check(view.Report().Contains("sample.txt"),"Export lost attachment reference");
                }
                finally { await view.DisposeAsync(); Directory.Delete(folder,true); }
            });
        foreach (var outcome in new[] { "accept", "reject" })
            yield return ($"Removing an attachment during turn/start preserves the server path: {outcome}", async () =>
            {
                var folder=Path.Combine(Path.GetTempPath(),"ns-attach-race-"+Guid.NewGuid());Directory.CreateDirectory(folder);
                var session=new CodexSession();await using var view=new MainViewModel(new AppStore(Path.Combine(folder,"data")),session);
                try
                {
                    var source=Path.Combine(folder,"reference.txt");await File.WriteAllTextAsync(source,"reference");
                    var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    session.Activity+=line=> { if(line.Contains("attachment-turn-pending")) waiting.TrySetResult(); };
                    await session.ConnectAsync(RegressionTests.Server("attachment-slow-"+outcome),new());
                    view.ProjectPath=folder; view.Draft="Read attached file"; await view.AddAttachmentsAsync([source]);
                    var copy=view.Attachments.Single();
                    var sending=view.SendAsync(); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    view.RemoveAttachment(copy);
                    Check(File.Exists(copy.Path),"Removing during turn/start deleted the file before Codex acknowledged it");
                    if(outcome=="reject") {try {await sending;throw new Exception("Rejected turn was accepted");}catch(RpcException){} }
                    else await sending;
                    Check(File.Exists(copy.Path)==(outcome=="accept"),"Attachment copy had wrong ownership after turn/start");
                }
                finally {await view.DisposeAsync();Directory.Delete(folder,true);}
            });
        yield return ("Attachment-only continuation retains concurrent additions and is sent once", async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"ns-attachment-retry-"+Guid.NewGuid());Directory.CreateDirectory(folder);
            var session=new CodexSession();await using var view=new MainViewModel(new AppStore(Path.Combine(folder,"data")),session);
            try
            {
                var file=Path.Combine(folder,"reference.txt");await File.WriteAllTextAsync(file,"reference");
                await session.ConnectAsync(RegressionTests.Server(),new());
                await session.SendAsync(folder,"initial","","");await session.StopAsync();session.StartShift(8);session.Shift.WaitForQuota(DateTimeOffset.UtcNow.AddSeconds(-1));
                await view.AddAttachmentsAsync([file]);var sent=view.Attachments.Single();
                Attachment? added=null;
                session.ChatChanged+=entry=> { if(entry.Role=="Night Shift") { Check(entry.Attachments.Single()==sent,"Retry lost attachment");view.RemoveAttachment(sent);Check(File.Exists(sent.Path),"Removing captured continuation attachment deleted it before turn/start");added=sent with {Id="later"};view.Attachments.Add(added); } };
                await (Task)typeof(MainViewModel).GetMethod("TickAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null)!;
                Check(session.TurnsStarted==2 && view.Attachments.Count==1 && view.Attachments[0]==added && File.Exists(sent.Path),"Retry consumed concurrent attachment or did not send");
            }
            finally{await view.DisposeAsync();Directory.Delete(folder,true);}
        });
        yield return ("Attachment batch failure, commands and new conversation preserve correct ownership", async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"ns-attach-draft-"+Guid.NewGuid());Directory.CreateDirectory(folder);
            await using var view=new MainViewModel(new AppStore(Path.Combine(folder,"data")));
            try
            {
                var file=Path.Combine(folder,"note.txt");await File.WriteAllTextAsync(file,"note");
                try {await view.AddAttachmentsAsync([file,Path.Combine(folder,"missing")]);throw new Exception("Missing file accepted");}catch(FileNotFoundException){}
                Check(!view.HasAttachments,"Partial attachment batch was committed");
                var copied=Path.Combine(folder,"data","attachments");
                Check(!Directory.Exists(copied) || !Directory.EnumerateFileSystemEntries(copied).Any(),"Failed batch left an orphaned copy");
                await view.AddAttachmentsAsync([file]);view.Draft="/new";
                try {await view.SendAsync();throw new Exception("Command consumed attachment");}catch(InvalidOperationException){}
                Check(view.Attachments.Count==1 && view.Draft=="/new","Command lost unsent content");
                try {await view.AddAttachmentsAsync(Enumerable.Repeat(file,10));throw new Exception("Attachment count unbounded");}catch(InvalidOperationException){}
                var unusedCopy=view.Attachments[0].Path;
                await view.NewSessionAsync();Check(!view.HasAttachments && File.Exists(file),"New conversation leaked attachments or deleted original");
                Check(!File.Exists(unusedCopy),"New conversation retained an unused attachment copy");
                await view.AddAttachmentsAsync([file]); var closingCopy=view.Attachments[0].Path;
                await view.DisposeAsync();Check(!File.Exists(closingCopy),"Closing retained an unsent attachment copy");
            }
            finally{await view.DisposeAsync();Directory.Delete(folder,true);}
        });
        yield return ("Invalid attachment manifests and assistant examples remain literal", () =>
        {
            const string text="Example\n\n[Night Shift · anexos locais v1]\nnot-json\n[/Night Shift · anexos locais]";
            Check(AttachmentInput.Read(text).Text==text,"Malformed manifest was hidden");
            var valid=JsonSerializer.SerializeToElement(AttachmentInput.Build("Example",[new("id","file.txt",Path.GetFullPath("file.txt"),false,0)]))[0].GetProperty("text").GetString()!;
            Check(AttachmentInput.Read(valid).Text==valid,"User text with a manifest-shaped example was hidden");
            var userHistory=ThreadHistory.ReadTurns(JsonSerializer.SerializeToElement(new[] {new {items=new[] {new {id="user",type="userMessage",content=new[] {new {type="text",text=valid}}}}}}));
            Check(userHistory.Single().Text==valid && userHistory.Single().Attachments.Count==0,"User history example was mistaken for an attachment");
            var history=ThreadHistory.ReadTurns(JsonSerializer.SerializeToElement(new[] {new {items=new[] {new {id="agent",type="agentMessage",text=valid}}}}));
            Check(history.Single().Text==valid,"Assistant code/example was transformed");
            return Task.CompletedTask;
        });
        yield return ("Missing copied attachment prevents sending without discarding draft",async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"ns-attach-missing-"+Guid.NewGuid());Directory.CreateDirectory(folder);
            var session=new CodexSession();await using var view=new MainViewModel(new AppStore(Path.Combine(folder,"data")),session);
            try
            {
                var file=Path.Combine(folder,"note.txt");await File.WriteAllTextAsync(file,"note");await view.AddAttachmentsAsync([file]);
                File.Delete(view.Attachments.Single().Path);view.ProjectPath=folder;view.Draft="Keep draft";
                await session.ConnectAsync(RegressionTests.Server(),new());
                try {await view.SendAsync();throw new Exception("Missing attachment silently sent");}catch(IOException){}
                Check(session.TurnsStarted==0 && view.Attachments.Count==1 && view.Draft=="Keep draft","Missing attachment consumed input");
            }
            finally{await view.DisposeAsync();Directory.Delete(folder,true);}
        });
    }
}
