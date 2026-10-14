using System.Text.Json;
using NightShift.Core;
using System.Diagnostics;
using System.Reflection;
if (args.Contains("--claude-fake-server")) { await ClaudeTests.RunServerAsync(args); return 0; }
if (args.Contains("--stall")) { await Task.Delay(5000); return 0; }
if (args.Contains("--fake-server")) { await FakeServer.RunAsync(); return 0; }
if (args.Contains("--shared-server")) { await SharedTests.RunServerAsync(); return 0; }
if (args.Length == 2 && args[0] == "--regression-server") { await RegressionTests.RunServerAsync(args[1]); return 0; }
if (args.Length == 3 && args[0] == "--real-protocol") return await RealProtocolProbe.RunAsync(args[1], args[2]);
if (args.Length == 2 && args[0] == "--real-socket") return await RealSocketProbe.RunAsync(args[1]);
if (args.Length == 2 && args[0] == "--real-attachments") return await RealAttachmentProbe.RunAsync(args[1]);

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action action) => tests.Add((name, action));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
var now = DateTimeOffset.FromUnixTimeSeconds(2000000000);
Test("Primary limit waits until actual reset plus margin", () => {
 var rates = RateSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":100,"resetsAt":2000001000},"secondary":null}}"""));
 Equal(now.AddSeconds(1015), rates.GetRetryAt(now));
 Equal(true, rates.IsBlocked);
});
Test("Secondary limit can outlast primary window", () => {
 var rates = RateSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":100,"resetsAt":2000000100},"secondary":{"usedPercent":100,"resetsAt":2000500000}}}"""));
 Equal(now.AddSeconds(500015), rates.GetRetryAt(now));
});
Test("Available budget ignores reset of unblocked window", () => {
 var rates = RateSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":20,"resetsAt":2000005000}}}"""));
 Equal(false,rates.IsBlocked); Equal<DateTimeOffset?>(null,rates.GetRetryAt(now));
});
Test("Codex bucket ignores unrelated depleted model bucket", () => {
 var rates = RateSnapshot.Parse(Json("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20}},"unrelated":{"primary":{"usedPercent":100,"resetsAt":2000000500}}}}"""));
 Equal(false, rates.IsBlocked);
});
Test("Unknown data does not assert availability", () => {
 Equal(false,RateSnapshot.Parse(Json("{}")).Known);
});
Test("Blocked window without reset polls in five minutes", () => {
 var rates=RateSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":100}}}"""));
 Equal(now.AddMinutes(5),rates.GetRetryAt(now));
});
Test("Expired blocked reset uses bounded backoff", () => {
 var rates=RateSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":100,"resetsAt":1999990000}}}"""));
 Equal(now.AddMinutes(5),rates.GetRetryAt(now));
});
Test("Stopped shift cannot retry even with available quota", () => {
 var shift=new ShiftState(); shift.Arm(now,8); shift.WaitForQuota(now.AddMinutes(5)); shift.Stop();
 Equal(false,shift.CanRetry(now.AddMinutes(10)));
});
Test("Completed turn does not automatically continue", () => {
 var shift=new ShiftState(); shift.Arm(now,8); shift.Complete();
 Equal(false,shift.CanRetry(now.AddMinutes(10)));
});
Test("Expired shift cannot retry", () => {
 var shift=new ShiftState(); shift.Arm(now,1); shift.WaitForQuota(now.AddMinutes(5));
 Equal(false,shift.CanRetry(now.AddHours(2)));
});
Test("Wait becomes eligible only at scheduled time", () => {
 var shift=new ShiftState(); shift.Arm(now,8); shift.WaitForQuota(now.AddMinutes(5));
 Equal(false,shift.CanRetry(now.AddMinutes(4))); Equal(true,shift.CanRetry(now.AddMinutes(5)));
});
Test("Store recovers corrupted settings and preserves file", () => {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()); Directory.CreateDirectory(path);
 try { File.WriteAllText(Path.Combine(path,"settings.json"),"broken{"); var store=new AppStore(path);
 var settings=store.LoadSettings(); Equal(8,settings.ShiftHours); Equal(true,Directory.GetFiles(path,"*.corrupt-*").Length>0);
 } finally { Directory.Delete(path,true); }
});
Test("Settings roundtrip never persists an armed shift", () => {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try { var store=new AppStore(path); store.SaveSettings(new AppSettings {ProjectPath="my project",AutoReview=false,ShiftHours=12});
 var saved=store.LoadSettings(); Equal(12,saved.ShiftHours); Equal(false,saved.AutoReview);
 var shift=new ShiftState(); Equal(false,shift.Armed);
 } finally { Directory.Delete(path,true); }
});
Test("Session roundtrip retains exact thread and Unicode", () => {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try { var store=new AppStore(path); store.SaveSession(new SessionRecord {ThreadId="thr_abc",ProjectPath="jogo ação",Messages=[new ChatEntry {Role="Você",Text="olá"}]});
 var saved=store.LoadSession("thr_abc")!; Equal("thr_abc",saved.ThreadId); Equal("olá",saved.Messages.Single().Text);
 } finally { Directory.Delete(path,true); }
});
Test("Corrupt session does not hide healthy sessions", () => {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try { var store=new AppStore(path); store.SaveSession(new SessionRecord {ThreadId="thr_good"}); File.WriteAllText(Path.Combine(path,"sessions","invalid.json"),"{");
 Equal(1,store.LoadSessions().Count);
 } finally { Directory.Delete(path,true); }
});
Test("Cache preserves every message independently of the visible history page", () => {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try { var store=new AppStore(path); store.SaveSession(new SessionRecord {ThreadId="thr_big",Messages=Enumerable.Range(0,600).Select(i=>new ChatEntry {Text=i.ToString()}).ToList()});
 var items=store.LoadSession("thr_big")!.Messages; Equal(600,items.Count); Equal("0",items.First().Text); Equal("599",items.Last().Text);
 } finally { Directory.Delete(path,true); }
});
Test("Session catalog does not load transcripts",()=> {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try {var store=new AppStore(path);for(int i=0;i<3;i++)store.SaveSession(new SessionRecord {ThreadId="catalog-"+i,Messages=[new ChatEntry {Text=new string('x',24000)}]});
 Equal(3,store.LoadSessions().Count); Equal(0,store.LoadSessions().Sum(r=>r.Messages.Count)); Equal(24000,store.LoadSession("catalog-1")!.Messages.Single().Text.Length);
 }finally{Directory.Delete(path,true);}
});
Test("Validated settings backup is restored for repeated loads",()=> {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try{var store=new AppStore(path);store.SaveSettings(new AppSettings {ShiftHours=12});store.SaveSettings(new AppSettings {ShiftHours=13});
 File.WriteAllText(Path.Combine(path,"settings.json"),"{");Equal(12,store.LoadSettings().ShiftHours);Equal(12,store.LoadSettings().ShiftHours);
 }finally{Directory.Delete(path,true);}
});
Test("Large Unicode transcript remains complete beyond the former file cap",()=> {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try{var store=new AppStore(path);store.SaveSession(new SessionRecord {ThreadId="unicode",Messages=Enumerable.Range(0,400).Select(i=>new ChatEntry {Text=new string('漢',24000)}).ToList()});
 var saved=store.LoadSession("unicode")!;Equal(400,saved.Messages.Count);Equal(24000,saved.Messages.Last().Text.Length);
 }finally{Directory.Delete(path,true);}
});
Test("Canonical history restores bounded messages and omits harness preamble",()=> {
 var raw=Json("""{"thread":{"turns":[{"items":[{"id":"u1","type":"userMessage","content":[{"type":"text","text":"do work"}]},{"id":"a1","type":"agentMessage","text":"all done"}]}]}}""");
 var history=ThreadHistory.Read(raw);Equal(2,history.Count);Equal("do work",history[0].Text);Equal("all done",history[1].Text);
});
var asyncTests = new List<(string Name, Func<Task> Run)>();
void AsyncTest(string name, Func<Task> run) => asyncTests.Add((name,run));
async Task Until(Func<bool> condition) {var end=Stopwatch.StartNew();while(!condition()){if(end.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("Expected session state was not reached");await Task.Delay(10);}}
ProcessStartInfo Server() { var info=new ProcessStartInfo(Environment.ProcessPath!); info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); info.ArgumentList.Add("--fake-server"); return info; }
AsyncTest("RPC preserves string request IDs for approval replies",async()=> {
 await using var rpc=new JsonRpcClient(Server());
 var reply=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
 rpc.MessageReceived+=m=> {if(m.TryGetProperty("method",out var v) && v.GetString()=="requestApproval") reply.TrySetResult(m);};
 var result=await rpc.RequestAsync("ask",new{}); Equal(true,result.GetProperty("ok").GetBoolean());
 var request=await reply.Task.WaitAsync(TimeSpan.FromSeconds(3));
 await rpc.ReplyAsync(request.GetProperty("id"),new {decision="decline"});
 var echo=await rpc.RequestAsync("lastReply",new{}); Equal("approval-007",echo.GetProperty("id").GetString());
});
AsyncTest("RPC matches concurrent out-of-order results",async()=> {
 await using var rpc=new JsonRpcClient(Server());
 var a=rpc.RequestAsync("echo",new {value="one"}); var b=rpc.RequestAsync("echo",new {value="two"});
 Equal("one",(await a).GetProperty("value").GetString()); Equal("two",(await b).GetProperty("value").GetString());
});
AsyncTest("RPC timeout does not hang later calls",async()=> {
 await using var rpc=new JsonRpcClient(Server()); bool timedOut=false;
 try{await rpc.RequestAsync("silent",new{},TimeSpan.FromMilliseconds(200));}catch(TimeoutException){timedOut=true;}
 Equal(true,timedOut); Equal("ok",(await rpc.RequestAsync("echo",new{value="ok"})).GetProperty("value").GetString());
});
AsyncTest("RPC process exit fails pending requests promptly",async()=> {
 await using var rpc=new JsonRpcClient(Server()); bool failed=false;
 try{await rpc.RequestAsync("exit",new{},TimeSpan.FromSeconds(3));}catch(IOException){failed=true;}
 Equal(true,failed);
});
AsyncTest("Supervisor streams and finishes without another turn",async()=> {
 await using var session=new CodexSession(); var received=new List<string>(); session.ChatChanged+=entry=>received.Add(entry.Text);
 await session.ConnectAsync(Server(),new AppSettings()); await session.SendAsync(Path.GetTempPath(),"hello","test","medium");
 await Task.Delay(200); Equal(false,session.IsBusy); Equal(true,received.Any(t=>t.Contains("Resposta")));
 await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1)); Equal(1,session.TurnsStarted);
});
AsyncTest("Supervisor resumes quota failure once and stays in same thread",async()=> {
 await using var session=new CodexSession(); await session.ConnectAsync(Server(),new AppSettings()); session.StartShift(8);
 await session.SendAsync(Path.GetTempPath(),"quota","test","medium"); await Task.Delay(200);
 Equal(true,session.Shift.WaitingForQuota); var thread=session.ThreadId;
 await Task.WhenAll(session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1)),session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1)));
 await Task.Delay(200); Equal(thread,session.ThreadId); Equal(2,session.TurnsStarted); Equal(false,session.Shift.WaitingForQuota);
});
AsyncTest("Stop during quota refresh prevents resume",async()=> {
 await using var session=new CodexSession(); await session.ConnectAsync(Server(),new AppSettings()); session.StartShift(8);
 await session.SendAsync(Path.GetTempPath(),"quota slow","test","medium"); await Until(()=>session.Shift.WaitingForQuota);
 Equal(true,session.Shift.WaitingForQuota);
 var retry=session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(1)); await Task.Delay(30); var stop=session.StopAsync();
 await Task.WhenAll(retry,stop); Equal(1,session.TurnsStarted); Equal(false,session.Shift.Armed);
});
AsyncTest("Unknown approval never executes by itself",async()=> {
 await using var session=new CodexSession(); await session.ConnectAsync(Server(),new AppSettings());
 await session.SendAsync(Path.GetTempPath(),"unknown approval","test","medium"); await Task.Delay(200);
 Equal(1,session.PendingRequests.Count); Equal(true,session.IsBusy);
 await session.StopAsync(); Equal(0,session.PendingRequests.Count);
});
AsyncTest("Reconnect resumes the previous thread before another send",async()=> {
 await using var session=new CodexSession(); await session.ConnectAsync(Server(),new AppSettings());
 await session.SendAsync(Path.GetTempPath(),"first","test","medium");await Task.Delay(200);var id=session.ThreadId;
 await session.ConnectAsync(Server(),new AppSettings());await session.SendAsync(Path.GetTempPath(),"second","test","medium");await Task.Delay(200);
 Equal(id,session.ThreadId);Equal(false,session.IsBusy);
});
AsyncTest("Blocked stdin has a bounded request timeout",async()=> {
 var start=Server();start.ArgumentList.Add("--stall");await using var rpc=new JsonRpcClient(start);bool timedOut=false;
 var timer=Stopwatch.StartNew();
 try{await rpc.RequestAsync("large",new{text=new string('x',2*1024*1024)},TimeSpan.FromMilliseconds(200));}
 catch(TimeoutException){timedOut=true;}
 Equal(true,timedOut);Equal(true,timer.Elapsed<TimeSpan.FromSeconds(1));
});
AsyncTest("Bootstrap permission can be answered before thread start returns",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());
 var path=Path.Combine(Path.GetTempPath(),"bootstrap-"+Guid.NewGuid().ToString());Directory.CreateDirectory(path);
 try {var sending=session.SendAsync(path,"first","test","medium"); await Until(()=>session.PendingRequests.Count>0);
 await session.ResolveRequestAsync(session.PendingRequests.Single(),new {action="decline",content=(object?)null}); await sending;
 }finally{Directory.Delete(path,true);}
});
Test("Storage retention discards older session payloads",()=> {
 var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
 try{var store=new AppStore(path,50000);for(int i=0;i<4;i++)store.SaveSession(new SessionRecord {ThreadId="retention-"+i,Messages=[new ChatEntry {Text=new string('x',22000)}]});
 Equal(true,store.LoadSessions().Count<4);Equal(true,store.LoadSession("retention-3") is not null);Equal(true,Directory.GetFiles(Path.Combine(path,"sessions")).Sum(f=>new FileInfo(f).Length)<=50000);
 }finally{Directory.Delete(path,true);}
});
Test("Native catalog rejects another project and preserves CLI origin",()=> {
 var project=Path.GetTempPath();
 var json=JsonSerializer.SerializeToElement(new {data=new object[]{new {id="cli-1",cwd=project,preview="Unity task",source="cli",updatedAt=1000},new {id="other",cwd=Path.Combine(project,"other"),preview="Wrong project",source="cli",updatedAt=2000}},nextCursor="page-2"});
 var page=SessionCatalog.Read(json,project);Equal(1,page.Sessions.Count);Equal("CLI",page.Sessions[0].SourceLabel);Equal("page-2",page.NextCursor);
});
AsyncTest("Latest native session uses project filter and canonical history",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());
 var record=await session.LatestSessionAsync(Path.GetTempPath());Equal("cli-history",record!.ThreadId);
 await session.ResumeAsync(record);Equal("cli-history",session.ThreadId);Equal("Original CLI conversation",session.RestoredHistory[0].Text);
 await session.SendAsync(record.ProjectPath,"next","test","medium");await Until(()=>!session.IsBusy);Equal("cli-history",session.ThreadId);
});
AsyncTest("Fork creates a different thread with original conversation",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());
 await session.ResumeAsync(new SessionRecord {ThreadId="cli-history",ProjectPath=Path.GetTempPath()});await session.ForkAsync();
 Equal("fork-history",session.ThreadId);Equal("Original CLI conversation",session.RestoredHistory[0].Text);
});
AsyncTest("Manual review and compaction cancel automatic continuation",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());
 await session.ResumeAsync(new SessionRecord {ThreadId="cli-history",ProjectPath=Path.GetTempPath()});session.StartShift(8);await session.ReviewAsync();
 await Until(()=>!session.IsBusy);Equal(false,session.Shift.Armed);session.StartShift(8);await session.CompactAsync();await Until(()=>!session.IsBusy);
 Equal(false,session.Shift.Armed);await session.CheckRetryAsync(DateTimeOffset.UtcNow.AddHours(2));Equal(0,session.TurnsStarted);
});
AsyncTest("Native history pages older turns before newer turns",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());
 await session.ResumeAsync(new SessionRecord {ThreadId="paged-history",ProjectPath=Path.GetTempPath()});
 Equal("Older",session.RestoredHistory[0].Text);Equal("Newer",session.RestoredHistory[1].Text);
});
Test("Command aliases identify resume without sending a model prompt",()=> {
 var c=CodexCommands.Parse("codex resume --last");Equal(true,c.IsCommand);Equal("resume",c.Definition!.Name);Equal("--last",c.Argument);
 Equal("resume",CodexCommands.Parse("codex --resume").Definition!.Name);Equal(false,CodexCommands.Parse("please resume my Unity task").IsCommand);
});
Test("Unsupported commands and options receive an error",()=> {
 Equal(true,CodexCommands.Parse("/unknown").Error is not null);Equal(true,CodexCommands.Parse("codex resume --all").Error is not null);
 Equal(true,CodexCommands.Parse("/resume --last; whoami").Error is not null);Equal(true,CodexCommands.Parse("/review --base main").Error is not null);
 Equal(true,CodexCommands.Parse("/compact extra").Error is not null);
});
AsyncTest("Git diff reads staged unstaged and untracked without changing files",async()=> {
 var path=Path.Combine(Path.GetTempPath(),"diff-"+Guid.NewGuid());Directory.CreateDirectory(path);
 async Task Git(params string[] arguments) {var start=new ProcessStartInfo("git"){WorkingDirectory=path};foreach(var a in arguments)start.ArgumentList.Add(a);using var process=Process.Start(start)!;await process.WaitForExitAsync();Equal(0,process.ExitCode);}
 try {await Git("init","-q");File.WriteAllText(Path.Combine(path,"tracked.txt"),"original\n");await Git("add","tracked.txt");
 await Git("-c","user.name=Test","-c","user.email=test@example.invalid","commit","-qm","first");
 File.WriteAllText(Path.Combine(path,"tracked.txt"),"staged\n");await Git("add","tracked.txt");File.AppendAllText(Path.Combine(path,"tracked.txt"),"unstaged\n");File.WriteAllText(Path.Combine(path,"new.txt"),"new content");
 await Git("config","diff.external","nonexistent-night-shift-diff");var diff=await ProjectDiff.ReadAsync(path);Equal(true,diff.Contains("staged"));Equal(true,diff.Contains("unstaged"));Equal(true,diff.Contains("new.txt"));
 Equal("staged\nunstaged\n",File.ReadAllText(Path.Combine(path,"tracked.txt")));
 }finally{foreach(var file in Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(path,true);}
});
AsyncTest("Manual action refused during active turn preserves busy state",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());await session.SendAsync(Path.GetTempPath(),"unknown approval","test","medium");await Until(()=>session.PendingRequests.Count>0);
 bool refused=false;try{await session.ReviewAsync();}catch(InvalidOperationException){refused=true;}Equal(true,refused);Equal(true,session.IsBusy);await session.StopAsync();
});
AsyncTest("Resume bootstrap request for different thread can be answered",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());await session.SendAsync(Path.GetTempPath(),"first","test","medium");await Until(()=>!session.IsBusy);
 var resuming=session.ResumeAsync(new SessionRecord {ThreadId="bootstrap-history",ProjectPath=Path.GetTempPath()});await Until(()=>session.PendingRequests.Count>0);
 await session.ResolveRequestAsync(session.PendingRequests.Single(),new {action="decline",content=(object?)null});await resuming;Equal("bootstrap-history",session.ThreadId);
});
AsyncTest("Older CLI falls back to canonical thread read",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());await session.ResumeAsync(new SessionRecord {ThreadId="legacy-history",ProjectPath=Path.GetTempPath()});
 Equal("Legacy canonical",session.RestoredHistory.Single().Text);
});
AsyncTest("Native catalog follows cursor and latest rejects repeated wrong-project pages",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());
 var first=await session.ListSessionsAsync(Path.GetTempPath());Equal("next-page",first.NextCursor);var second=await session.ListSessionsAsync(Path.GetTempPath(),first.NextCursor);Equal("older-history",second.Sessions[0].ThreadId);
 var path=Path.Combine(Path.GetTempPath(),"catalog-loop-"+Guid.NewGuid());Directory.CreateDirectory(path);
 try{Equal<SessionRecord?>(null,await session.LatestSessionAsync(path));}finally{Directory.Delete(path,true);}
});
AsyncTest("Fork bootstrap request accepts the new thread identity",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());await session.ResumeAsync(new SessionRecord {ThreadId="fork-bootstrap-source",ProjectPath=Path.GetTempPath()});
 var forking=session.ForkAsync();await Until(()=>session.PendingRequests.Count>0);await session.ResolveRequestAsync(session.PendingRequests.Single(),new {action="decline",content=(object?)null});await forking;Equal("fork-history",session.ThreadId);
});
AsyncTest("Transcript failure keeps resumed thread usable with a warning",async()=> {
 await using var session=new CodexSession();var warnings=new List<string>();session.Warning+=warnings.Add;await session.ConnectAsync(Server(),new AppSettings());
 await session.ResumeAsync(new SessionRecord {ThreadId="unavailable-history",ProjectPath=Path.GetTempPath()});Equal("unavailable-history",session.ThreadId);Equal(0,session.RestoredHistory.Count);Equal(true,warnings.Count>0);
});
AsyncTest("Saving during resume history loading preserves destination cache",async()=> {
 var path=Path.Combine(Path.GetTempPath(),"vm-"+Guid.NewGuid());var store=new AppStore(path);var session=new CodexSession();await using var view=new NightShift.App.MainViewModel(store,session);
 try {await session.ConnectAsync(Server(),new AppSettings());await view.ResumeAsync(new SessionRecord {ThreadId="cli-history",ProjectPath=Path.GetTempPath()});
 store.SaveSession(new SessionRecord {ThreadId="delayed-history",ProjectPath=Path.GetTempPath(),Title="Destination",Messages=[new ChatEntry {Text="Destination cache"}]});
 var switching=view.ResumeAsync(new SessionRecord {ThreadId="delayed-history",ProjectPath=Path.GetTempPath(),Title="Destination"});await Until(()=>session.ThreadId=="delayed-history");view.Save();
 Equal("Destination cache",store.LoadSession("delayed-history")!.Messages.Single().Text);await view.DisposeAsync();try{await switching;}catch(OperationCanceledException) { /* Closing cancels the in-flight history request. */ }
 Equal("Destination cache",store.LoadSession("delayed-history")!.Messages.Single().Text);
 }finally{await view.DisposeAsync();Directory.Delete(path,true);}
});
AsyncTest("Command stop remains usable while generation is running",async()=> {
 var path=Path.Combine(Path.GetTempPath(),"vm-"+Guid.NewGuid());var session=new CodexSession();await using var view=new NightShift.App.MainViewModel(new AppStore(path),session);
 try {await session.ConnectAsync(Server(),new AppSettings());await session.SendAsync(Path.GetTempPath(),"unknown approval","test","medium");await Until(()=>session.IsBusy && session.PendingRequests.Count>0);
 Equal(true,view.Commands.Single(c=>c.Name=="stop").CanExecute);Equal(false,view.Commands.Single(c=>c.Name=="review").CanExecute);view.Draft="/stop";Equal(true,view.CanSend);await view.SendAsync();Equal(false,session.IsBusy);
 }finally{await view.DisposeAsync();Directory.Delete(path,true);}
});
AsyncTest("Fork only accepts destination requests during delayed history",async()=> {
 await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());await session.ResumeAsync(new SessionRecord {ThreadId="fork-noise-source",ProjectPath=Path.GetTempPath()});
 await session.ForkAsync();await Task.Delay(50);Equal(0,session.PendingRequests.Count);
});
AsyncTest("History includes Night Shift app-server conversations",async()=> {
 var path=Path.Combine(Path.GetTempPath(),"appserver-catalog-"+Guid.NewGuid());Directory.CreateDirectory(path);
 try{await using var session=new CodexSession();await session.ConnectAsync(Server(),new AppSettings());var result=await session.ListSessionsAsync(path);Equal(1,result.Sessions.Count);Equal("appServer",result.Sessions[0].Source);}
 finally{Directory.Delete(path,true);}
});
AsyncTest("Saving during fork history does not persist the previous display as the fork",async()=> {
 var path=Path.Combine(Path.GetTempPath(),"vm-"+Guid.NewGuid());var store=new AppStore(path);var session=new CodexSession();await using var view=new NightShift.App.MainViewModel(store,session);
 try {await session.ConnectAsync(Server(),new AppSettings());await view.ResumeAsync(new SessionRecord {ThreadId="fork-cache-source",ProjectPath=Path.GetTempPath()});
 var forking=view.RunCommandAsync("/fork");await Until(()=>session.ThreadId=="fork-history");view.Save();Equal<SessionRecord?>(null,store.LoadSession("fork-history"));await forking;Equal(true,store.LoadSession("fork-history")!.Title.StartsWith("Cópia"));
 }finally{await view.DisposeAsync();Directory.Delete(path,true);}
});
AsyncTest("Unsupported GUI chat command is preserved and never sent as a prompt",async()=> {
 var path=Path.Combine(Path.GetTempPath(),"vm-"+Guid.NewGuid());var session=new CodexSession();await using var view=new NightShift.App.MainViewModel(new AppStore(path),session);
 try {await session.ConnectAsync(Server(),new AppSettings());view.ProjectPath=Path.GetTempPath();view.Draft="/cloud unsupported";await view.RunAsync(view.SendAsync);Equal(0,session.TurnsStarted);Equal("/cloud unsupported",view.Draft);Equal(true,view.Notice.Contains("não disponível"));}
 finally{await view.DisposeAsync();Directory.Delete(path,true);}
});
int failures=0;
asyncTests.AddRange(ClaudeTests.Cases());
asyncTests.AddRange(ProviderUiTests.Cases());
asyncTests.AddRange(SlimTests.Cases());
asyncTests.AddRange(ProviderStoreTests.Cases());
asyncTests.AddRange(SharedTests.Cases());
asyncTests.AddRange(RegressionTests.Cases());
asyncTests.AddRange(ContinuationTests.Cases());
asyncTests.AddRange(AttachmentTests.Cases());
asyncTests.AddRange(SteeringTests.Cases());
asyncTests.AddRange(RecentTests.Cases());
asyncTests.Add(("WebSocket handles fragmented Unicode and fails pending requests on disconnect", WebSocketTests.FragmentationAndDisconnectAsync));
foreach(var (name,run) in tests) {try{run();Console.WriteLine($"PASS {name}");}catch(Exception ex){failures++;Console.WriteLine($"FAIL {name}: {ex.Message}");}}
foreach(var (name,run) in asyncTests) {try{await run();Console.WriteLine($"PASS {name}");}catch(Exception ex){failures++;Console.WriteLine($"FAIL {name}: {ex.Message}");}}
Console.WriteLine($"{tests.Count+asyncTests.Count-failures}/{tests.Count+asyncTests.Count} passed");
return failures==0?0:1;
