using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NightShift.Core;
internal static class ClaudeTests
{
    static void Check(bool value,string message) {if(!value)throw new Exception(message);}
    static async Task Until(Func<bool> predicate) {for(int i=0;i<300;i++){if(predicate())return;await Task.Delay(10);}throw new Exception("Claude fixture timed out");}
    static ProcessStartInfo Server()
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!);
        if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")start.ArgumentList.Add(typeof(ClaudeTests).Assembly.Location);
        start.ArgumentList.Add("--claude-fake-server");return start;
    }
    public static IEnumerable<(string,Func<Task>)> Cases()
    {
        foreach(var oversizedFile in new[]{false,true})
        yield return (oversizedFile?"Claude oversized native file restores cached text and image metadata honestly":"Claude oversized native image row restores cached text and image metadata honestly",async()=> {
            var root=Path.Combine(Path.GetTempPath(),"claude-large-history-"+Guid.NewGuid());
            var config=Path.Combine(root,"config");var project=Path.Combine(config,"projects","project");Directory.CreateDirectory(project);
            var previous=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR",config);
            try {
                var id=Guid.NewGuid().ToString();var attachmentId=Guid.NewGuid().ToString("N");
                var image=new Attachment(attachmentId,"image.png",Path.Combine(root,"attachments",attachmentId,"image.png"),true,7*1024*1024);
                var text=JsonSerializer.SerializeToElement(AttachmentInput.Build("Preserved user text",[image])[0]).GetProperty("text").GetString();
                var native=JsonSerializer.Serialize(new {type="user",sessionId=id,cwd=Path.GetTempPath(),uuid="user-large",message=new {content=new object[]{new{type="text",text},new{type="image",source=new{type="base64",media_type="image/png",data=new string('A',10*1024*1024)}}}}});
                var file=Path.Combine(project,id+".jsonl");
                using(var output=new StreamWriter(file)) {
                    for(int i=0;i<(oversizedFile?7:1);i++)output.WriteLine(native);
                    output.WriteLine(JsonSerializer.Serialize(new{type="assistant",sessionId=id,cwd=Path.GetTempPath(),uuid="assistant-small",message=new{content="Preserved reply"}}));
                }
                var store=new AppStore(Path.Combine(root,"store"));var messages=Enumerable.Range(0,oversizedFile?7:1).Select(i=>new ChatEntry{Id="cached-"+i,Role="Você",Text="Preserved user text",Attachments=[image]}).ToList();
                messages.Add(new ChatEntry{Id="assistant-small",Role="Claude",Text="Preserved reply"});
                store.SaveSession(new SessionRecord {Provider=AgentProvider.Claude,ThreadId=id,ProjectPath=Path.GetTempPath(),Messages=messages});
                await using var session=new ClaudeSession(store);var warnings=new List<string>();session.Warning+=warnings.Add;
                var restored=await session.ReadSessionAsync(id);
                Check(restored.Messages.Count==messages.Count && restored.Messages.Where(m=>m.Role=="Você").All(m=>m.Text=="Preserved user text" && m.Attachments.Single().Id==attachmentId),"Native limits replaced complete cached text/image history");
                Check(warnings.Any(w=>w.Contains("cache",StringComparison.OrdinalIgnoreCase)),"Fallback was not reported as cached history");
                await session.ConnectAsync(Server(),new AppSettings{AutoReview=false});await session.ResumeAsync(restored);
                Check(session.RestoredHistory.Count==messages.Count && session.RestoredHistory.Where(m=>m.Role=="Você").All(m=>m.Attachments.Single().Id==attachmentId),"Resume discarded cached image history");
                await using var uncached=new ClaudeSession();bool rejected=false;try{await uncached.ReadSessionAsync(id);}catch(IOException){rejected=true;}
                Check(rejected,"Oversized native history silently returned incomplete messages without a cache");
            }finally{Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR",previous);Directory.Delete(root,true);}
        });
        yield return ("Claude ambiguous acceptance timeout closes owned worker and preserves draft without retry",async()=> {
            var root=Path.Combine(Path.GetTempPath(),"claude-ambiguous-"+Guid.NewGuid());Directory.CreateDirectory(root);
            var marker=Path.Combine(root,"owner.json");var start=Server();start.ArgumentList.Add("--claude-owner-file");start.ArgumentList.Add(marker);
            var store=new AppStore(Path.Combine(root,"store"));store.SaveSettings(new AppSettings{Provider=AgentProvider.Claude,ProjectPath=Path.GetTempPath(),AutoReview=false});
            var session=new ClaudeSession(store);await using var view=new NightShift.App.MainViewModel(store,session);
            try {
                await session.ConnectAsync(start,new AppSettings{AutoReview=false});session.StartShift(8);view.Draft="ambiguous timeout";
                using var owner=JsonDocument.Parse(await File.ReadAllTextAsync(marker));using var process=Process.GetProcessById(owner.RootElement.GetProperty("pid").GetInt32());
                bool timedOut=false;try{await view.SendAsync();}catch(TimeoutException){timedOut=true;}
                Check(timedOut && view.Draft=="ambiguous timeout" && session.TurnsStarted==0,"Uncertain send consumed draft or accepted a turn");
                Check(!session.IsConnected && !session.IsBusy && !session.Shift.Armed && process.HasExited,"Uncertain send left owned worker live or continuation armed");
                Check(session.Status.Contains("incerto",StringComparison.OrdinalIgnoreCase),"Uncertain execution status not explained");
                await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1));bool refused=false;try{await session.SendAsync(Path.GetTempPath(),"second","","");}catch(InvalidOperationException){refused=true;}
                await Task.Delay(100);Check(refused && session.PendingRequests.Count==0 && !view.Messages.Any(e=>e.Text.Contains("stale uncertain")),"Stale worker events or a second send escaped fail-closed state");
            }finally{await view.DisposeAsync();Directory.Delete(root,true);}
        });
        yield return ("Claude native stream reassembles Unicode chunks without duplicate final text",async()=> {
            await using var session=new ClaudeSession();var entries=new Dictionary<string,ChatEntry>();session.ChatChanged+=e=>entries[e.Id]=e;
            await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
            Check(await session.SendAsync(Path.GetTempPath(),"stream","sonnet","medium"),"Input not accepted");
            await Until(()=>!session.IsBusy);
            Check(entries.Values.Single(e=>e.Role=="Claude").Text=="Olá 🐈 café","Unicode/delta duplicate");
            Check(!session.Shift.WaitingForQuota,"Completed task armed retry");
        });
        yield return ("Claude accepts native image replay above eight megabytes without shell prompt arguments",async()=> {
            var file=Path.Combine(Path.GetTempPath(),"claude-image-"+Guid.NewGuid()+".png");
            try {
                await File.WriteAllBytesAsync(file,new byte[7*1024*1024]);
                var attachment=new Attachment(Guid.NewGuid().ToString("N"),Path.GetFileName(file),file,true,new FileInfo(file).Length);
                await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
                Check(await session.SendAsync(Path.GetTempPath(),"image native","","",[attachment]),"Native image input not accepted");
                await Until(()=>!session.IsBusy);Check(session.IsConnected,"Valid image replay disconnected stream");
            }finally{File.Delete(file);}
        });
        yield return ("Claude Send returns when input accepted while permission waits",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
            Check(await session.SendAsync(Path.GetTempPath(),"permission","",""),"Input not accepted");
            await Until(()=>session.PendingRequests.Count==1);Check(session.IsBusy,"Turn completed before permission");
            var request=session.PendingRequests.Single();Check(request.Method=="claude/permission","Permission not mapped");
            await session.ResolveRequestAsync(request,new {behavior="deny",message="User declined"});await Until(()=>!session.IsBusy);
            Check(!session.Shift.WaitingForQuota,"Denial caused quota retry");
        });
        yield return ("Claude AskUserQuestion replies with updated native input",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
            await session.SendAsync(Path.GetTempPath(),"question","","");await Until(()=>session.PendingRequests.Count==1);
            var request=session.PendingRequests.Single();Check(request.Method=="claude/question","Question not mapped");
            await session.ResolveRequestAsync(request,new {answers=new Dictionary<string,string>{{"Format?","Brief"}}});await Until(()=>!session.IsBusy);
            Check(session.PendingRequests.Count==0,"Question left pending");
        });
        yield return ("Claude protocol corruption releases pending requests and connection",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
            await session.SendAsync(Path.GetTempPath(),"malformed","","");await Until(()=>!session.IsConnected);
            Check(!session.IsBusy && session.PendingRequests.Count==0,"Corrupt protocol retained state");
        });
        yield return ("Claude disconnect releases permission and never continues",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});session.StartShift(8);
            await session.SendAsync(Path.GetTempPath(),"disconnect","","");await Until(()=>!session.IsConnected);
            await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1));Check(!session.IsBusy && session.PendingRequests.Count==0 && session.TurnsStarted==1,"Disconnected worker retained pending state or retried");
        });
        yield return ("Claude only rejected timer limits arm continuation with unavailable percentage",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});session.StartShift(8);
            await session.SendAsync(Path.GetTempPath(),"quota","","");await Until(()=>!session.IsBusy);
            Check(session.Shift.WaitingForQuota && session.Rates.Primary is null,"Quota missing utilization invented percentage");
            Check(session.Rates.ServerBlocked,"Rejected limit not recorded");
            await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(6));await Until(()=>!session.IsBusy);
            Check(session.TurnsStarted==2 && !session.Shift.WaitingForQuota,"Quota didn't resume once or completed task retried");
        });
        yield return ("Claude credits and ordinary errors never arm quota continuation",async()=> {
            foreach(var text in new[]{"credits","network","warning"}) {
                await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});session.StartShift(8);
                await session.SendAsync(Path.GetTempPath(),text,"","");await Until(()=>!session.IsBusy);
                await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1));Check(!session.Shift.WaitingForQuota && session.TurnsStarted==1,"Non timer failure resumed: "+text);
            }
        });
        yield return ("Claude stop during captured retry draft cancels send and preserves acceptance",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});session.StartShift(8);
            await session.SendAsync(Path.GetTempPath(),"quota","","");await Until(()=>!session.IsBusy);bool accepted=false,finished=false;
            Task? stop=null;
            await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddMinutes(6),()=> {stop=session.StopAsync();return new("draft",()=>accepted=true,null,()=>finished=true);});
            if(stop is not null)await stop;
            Check(session.TurnsStarted==1 && !accepted && finished && !session.Shift.Armed,"Stop raced continuation");
        });
        yield return ("Claude external discovery is observer only with no takeover",async()=> {
            await using var session=new ClaudeSession();await session.ConnectSharedAsync(Server(),new AppSettings());
            var records=await session.ListLiveSessionsAsync(Path.GetTempPath());Check(records.Count==2,"External list missing rows");
            await session.AttachLiveAsync(records.Single(r=>r.ExternalKind=="background"));
            Check(session.IsReadOnly && session.CanOpenOrigin && session.IsBusy && !session.CanAcceptSharedInput,"External capabilities unsafe");
            bool rejected=false;try{await session.SendAsync(Path.GetTempPath(),"take over","","");}catch(NotSupportedException){rejected=true;}
            Check(rejected && session.TurnsStarted==0,"External worker accepts redirected prompt");
            await session.DisconnectAsync();Check(!session.IsConnected,"Observer didn't disconnect");
        });
        yield return ("Claude interactive agent without short id is visible and blocks history takeover",async()=> {
            var start=Server();start.ArgumentList.Add("--claude-interactive-no-id");
            await using var observer=new ClaudeSession();await observer.ConnectSharedAsync(start,new AppSettings());
            var row=(await observer.ListLiveSessionsAsync(Path.GetTempPath())).SingleOrDefault(r=>r.ThreadId=="interactive-session-only");
            Check(row is not null && row.ExternalKind=="interactive","Official interactive sessionId row without id was dropped");
            await observer.AttachLiveAsync(row!);Check(observer.IsReadOnly && !observer.CanOpenOrigin,"Interactive session acquired background control");
            await using var managed=new ClaudeSession();await managed.ConnectAsync(start,new AppSettings{AutoReview=false});bool refused=false;
            try{await managed.ResumeAsync(new SessionRecord{Provider=AgentProvider.Claude,ThreadId="interactive-session-only",ProjectPath=Path.GetTempPath()});}catch(InvalidOperationException){refused=true;}
            Check(refused && managed.TurnsStarted==0,"Managed resume duplicated native interactive owner without id");
        });
        yield return ("Claude refuses history resume while external session is alive",async()=> {
            await using var session=new ClaudeSession();await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
            bool rejected=false;try{await session.ResumeAsync(new SessionRecord {Provider=AgentProvider.Claude,ThreadId="live-background",ProjectPath=Path.GetTempPath()});}catch(InvalidOperationException){rejected=true;}
            Check(rejected && session.TurnsStarted==0,"Resume duplicated live worker");
        });
        yield return ("Claude can restart its own idle worker to change model without duplicating foreign sessions",async()=> {
            var marker=Path.Combine(Path.GetTempPath(),"claude-owner-"+Guid.NewGuid()+".json");
            var start=Server();start.ArgumentList.Add("--claude-owner-file");start.ArgumentList.Add(marker);
            try {
                await using var session=new ClaudeSession();await session.ConnectAsync(start,new AppSettings {AutoReview=false});
                await session.SendAsync(Path.GetTempPath(),"stream","sonnet","medium");await Until(()=>!session.IsBusy);
                var id=session.ThreadId;
                Check(await session.SendAsync(Path.GetTempPath(),"stream","opus","high"),"Own idle session blocked model change");await Until(()=>!session.IsBusy);
                Check(session.ThreadId==id && session.TurnsStarted==2,"Model change forked or duplicated session");
                await session.ResumeAsync(new SessionRecord {Provider=AgentProvider.Claude,ThreadId=id!,ProjectPath=Path.GetTempPath()});
                Check(session.ThreadId==id,"Own session resume changed identity");
            }finally{File.Delete(marker);}
        });
        yield return ("Claude history listing returns metadata without hydrating every transcript",()=> {
            var root=Path.Combine(Path.GetTempPath(),"claude-catalog-"+Guid.NewGuid());var project=Path.Combine(root,"project");Directory.CreateDirectory(project);
            try {
                var id=Guid.NewGuid().ToString();var file=Path.Combine(project,id+".jsonl");
                File.WriteAllLines(file,[JsonSerializer.Serialize(new {type="user",sessionId=id,cwd=Path.GetTempPath(),uuid="u",message=new {content="Original"}}),JsonSerializer.Serialize(new {type="assistant",sessionId=id,uuid="a",message=new {content=new[]{new {type="text",text=new string('a',100000)}}}})]);
                var metadata=ClaudeCatalog.ReadHistory(root).Single();Check(metadata.Title=="Original" && metadata.Messages.Count==0,"Catalog hydrated full transcript while listing");
                var selected=ClaudeCatalog.ReadRecord(id,root)!;Check(selected.Messages.Count==2&&selected.Messages.Last().Text.Length==100000,"Selected canonical transcript not loaded");
            }finally{Directory.Delete(root,true);}return Task.CompletedTask;
        });
        yield return ("Claude caches and restores managed history by provider",async()=> {
            var root=Path.Combine(Path.GetTempPath(),"claude-history-"+Guid.NewGuid());
            try {
                var store=new AppStore(root);store.SaveSession(new SessionRecord {Provider=AgentProvider.Claude,ThreadId="completed-history",ProjectPath=Path.GetTempPath(),Messages=[new ChatEntry {Role="Claude",Text="Earlier"}]});
                await using var session=new ClaudeSession(store);await session.ConnectAsync(Server(),new AppSettings {AutoReview=false});
                await session.ResumeAsync(await session.ReadSessionAsync("completed-history"));Check(session.RestoredHistory.Single().Text=="Earlier","Resume lost cache");
                Check(await session.SendAsync(Path.GetTempPath(),"stream","", ""),"Resumed input not accepted");await Until(()=>!session.IsBusy);
            }finally{Directory.Delete(root,true);}
        });
    }
    static async Task Emit(object value) {await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));await Console.Out.FlushAsync();}
    public static async Task RunServerAsync(string[] args)
    {
        if(args.Contains("agents")) {
            var records=new List<object>();
            if(args.Contains("--claude-interactive-no-id"))records.Add(new{sessionId="interactive-session-only",pid=12345,cwd=Path.GetTempPath(),kind="interactive",status="waiting"});
            var markerIndex=Array.IndexOf(args,"--claude-owner-file");
            if(markerIndex>=0&&File.Exists(args[markerIndex+1])) {
                using var owner=JsonDocument.Parse(File.ReadAllText(args[markerIndex+1]));
                try {
                    using var process=Process.GetProcessById(owner.RootElement.GetProperty("pid").GetInt32());
                    if(!process.HasExited)records.Add(new {id="owned-worker",sessionId=owner.RootElement.GetProperty("sessionId").GetString(),cwd=Path.GetTempPath(),kind="interactive",status="idle",startedAt=1700000000000L});
                }catch(ArgumentException){}
            }
            records.AddRange(new object[]{new {id="abc123",sessionId="live-background",cwd=Path.GetTempPath(),kind="background",state="working",startedAt=1700000000000L},new {id="terminal123",sessionId="live-interactive",cwd=Path.GetTempPath(),kind="interactive",status="waiting",waitingFor="approval",startedAt=1700000000001L}});await Emit(records);return;
        }
        Check(args.Contains("--permission-prompt-tool") && args.Contains("stdio") && args.Contains("--replay-user-messages"),"Missing native safe stream flags");
        Check(!args.Any(a=>a.Contains("bypass",StringComparison.OrdinalIgnoreCase)),"Permission bypass");
        var ownIndex=Array.IndexOf(args,"--claude-owner-file");
        if(ownIndex>=0)File.WriteAllText(args[ownIndex+1],JsonSerializer.Serialize(new {pid=Environment.ProcessId,sessionId=args.FirstOrDefault(a=>a.StartsWith("--resume="))?[9..]??args.FirstOrDefault(a=>a.StartsWith("--session-id="))?[13..]}));
        string? permission=null;
        while(await Console.In.ReadLineAsync() is { } line) {
            using var doc=JsonDocument.Parse(line);var message=doc.RootElement;var type=CodexSession.Text(message,"type");
            if(type=="control_request") {
                await Emit(new {type="control_response",response=new {subtype="success",request_id=message.GetProperty("request_id").GetString(),response=new {models=new[]{new {value="sonnet",displayName="Sonnet",supportedEffortLevels=new[]{"low","medium","high"}}}}}});continue;
            }
            if(type=="control_response") {
                var reply=message.GetProperty("response");Check(reply.GetProperty("request_id").GetString()=="permission-1","Mismatched control ID");
                var response=reply.GetProperty("response");
                if(permission=="question")Check(response.GetProperty("updatedInput").GetProperty("answers").GetProperty("Format?").GetString()=="Brief","Question native input not updated");
                else Check(CodexSession.Text(response,"behavior")=="deny","User denial lost");
                await Emit(new {type="result",subtype="success",is_error=false,result="Done"});continue;
            }
            if(type!="user")continue;
            var inputText=message.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
            if(inputText.Contains("ambiguous timeout")) {
                await Task.Delay(TimeSpan.FromSeconds(16));
                await Emit(new {type="assistant",message=new{id="stale",content=new[]{new{type="text",text="stale uncertain"}}}});
                await Emit(new {type="control_request",request_id="stale",request=new{subtype="can_use_tool",tool_name="Bash",input=new{command="stale"}}});continue;
            }
            await Emit(message);
            var text=message.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
            await Emit(new {type="system",subtype="init",session_id=args.FirstOrDefault(a=>a.StartsWith("--resume="))?[9..]??args.FirstOrDefault(a=>a.StartsWith("--session-id="))?[13..]??"fixture-session"});
            if(text.Contains("malformed")){await Console.Out.WriteLineAsync("{broken");await Console.Out.FlushAsync();continue;}
            if(text.Contains("permission")||text.Contains("question")||text.Contains("disconnect")) {
                permission=text.Contains("question")?"question":"permission";
                var input=permission=="question"?JsonSerializer.SerializeToElement(new {questions=new[]{new {question="Format?",header="Format",options=new[]{new {label="Brief",description="Short"}},multiSelect=false}}}):JsonSerializer.SerializeToElement(new {command="echo test"});
                await Emit(new {type="control_request",request_id="permission-1",request=new {subtype="can_use_tool",tool_name=permission=="question"?"AskUserQuestion":"Bash",input}});
                if(text.Contains("disconnect"))return;continue;
            }
            if(text.Contains("quota")||text.Contains("credits")||text.Contains("warning")) {
                await Emit(new {type="rate_limit_event",rate_limit_info=new {status=text.Contains("warning")?"allowed_warning":"rejected",errorCode=text.Contains("credits")?"credits_required":(string?)null}});
                await Emit(new {type="result",subtype=text.Contains("warning")?"success":"error_during_execution",is_error=!text.Contains("warning"),errors=new[]{"fixture failure"}});continue;
            }
            if(text.Contains("network")){await Emit(new {type="result",subtype="error_during_execution",is_error=true,errors=new[]{"network unavailable"}});continue;}
            await Emit(new {type="stream_event", @event=new {type="message_start",message=new {id="assistant-1"}}});
            var chunk="""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Olá 🐈 café"}}}"""+"\n";
            var bytes=Encoding.UTF8.GetBytes(chunk);var output=Console.OpenStandardOutput();for(int i=0;i<bytes.Length;i+=3){await output.WriteAsync(bytes.AsMemory(i,Math.Min(3,bytes.Length-i)));await output.FlushAsync();}
            await Emit(new {type="assistant",message=new {id="assistant-1",content=new[]{new {type="text",text="Olá 🐈 café"}}}});
            await Emit(new {type="result",subtype="success",is_error=false,result="Olá 🐈 café"});
        }
    }
}
