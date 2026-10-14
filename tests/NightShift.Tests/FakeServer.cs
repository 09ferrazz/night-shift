using System.Text.Json;
internal static class FakeServer
{
 public static async Task RunAsync()
 {
  int turns=0; int loopRequests=0; bool slow=false; bool loaded=false; string currentThread="thread-main"; bool forkNoise=false; bool forkCache=false; JsonElement? firstEchoId=null; JsonElement last=JsonDocument.Parse("{}").RootElement.Clone();
  async Task Emit(object payload) {Console.WriteLine(JsonSerializer.Serialize(payload)); await Console.Out.FlushAsync();}
  async Task Event(string method,object data) => await Emit(new {method,@params=data});
  while(await Console.In.ReadLineAsync() is { } line)
  {
   var m=JsonDocument.Parse(line).RootElement;
   if(!m.TryGetProperty("method",out var mv)) {last=m.Clone();continue;}
   var method=mv.GetString(); if(!m.TryGetProperty("id",out var id))continue;
   var p=m.TryGetProperty("params",out var pv)?pv:JsonDocument.Parse("{}").RootElement;
   if(method=="exit") return; if(method=="silent")continue;
   object result=new{};
   if(method=="echo" && p.GetProperty("value").GetString()=="one") {firstEchoId=id.Clone(); continue;}
   if(method=="echo") {result=p; if(firstEchoId is {} held) {await Emit(new {id,result}); await Emit(new {id=held,result=new {value="one"}}); firstEchoId=null; continue;}}
   if(method=="ask") {result=new {ok=true}; await Emit(new {id="approval-007",method="requestApproval",@params=new {}});}
   if(method=="lastReply")result=last;
   if(method=="thread/start" && p.GetProperty("cwd").GetString()!.Contains("bootstrap-")) {await Emit(new{id="bootstrap-1",method="mcpServer/elicitation/request",@params=new{threadId="thread-main",mode="form",message="test"}});var answer=await Console.In.ReadLineAsync();if(answer is null)return;}
   if(method=="thread/resume" && p.GetProperty("threadId").GetString()=="bootstrap-history") {await Emit(new {id="resume-bootstrap-1",method="mcpServer/elicitation/request",@params=new {threadId="bootstrap-history",mode="form",message="test"}});if(await Console.In.ReadLineAsync() is null)return;}
   if(method=="thread/fork" && currentThread=="fork-bootstrap-source") {await Emit(new {id="fork-bootstrap-1",method="mcpServer/elicitation/request",@params=new {threadId="fork-history",mode="form",message="test"}});if(await Console.In.ReadLineAsync() is null)return;}
   if(method=="thread/start" || method=="thread/resume" || method=="thread/fork") {
    forkCache=method=="thread/fork" && currentThread=="fork-cache-source";
    forkNoise=method=="thread/fork" && currentThread=="fork-noise-source";
    loaded=true; currentThread=method=="thread/start"?"thread-main":method=="thread/fork"?"fork-history":p.GetProperty("threadId").GetString()!;
    if(p.GetProperty("sandbox").GetString()!="workspace-write" || p.GetProperty("approvalPolicy").GetString()!="on-request")throw new Exception("Policy changed");
    result=new {thread=new {id=currentThread,turns=Array.Empty<object>()}};
   }
   if(method=="thread/list") {
    if(p.GetProperty("sortKey").GetString()!="updated_at")throw new Exception("Wrong history order");
    var cwd=p.GetProperty("cwd").GetString()??Path.GetTempPath();
    if(cwd.Contains("appserver-catalog-")) {var included=p.TryGetProperty("sourceKinds",out var sources) && sources.EnumerateArray().Any(s=>s.GetString()=="appServer");
     result=new {data=included?new[]{new {id="own-appserver",cwd,source="appServer",preview="Own",updatedAt=3000}}:[],nextCursor=(string?)null};await Emit(new {id,result});continue;}
    var next=p.GetProperty("cursor").ValueKind!=JsonValueKind.Null;
    if(cwd.Contains("catalog-loop-")) {loopRequests++; if(loopRequests>2)throw new Exception("Repeated cursor was followed");result=new {data=new[]{new {id="wrong-project",cwd=Path.Combine(cwd,"other"),source="cli",preview="Wrong",updatedAt=2000}},nextCursor="loop"};}
    else result=new {data=new[]{new {id=next?"older-history":"cli-history",cwd,source="cli",preview="Unity conversation",updatedAt=next?1000:2000}},nextCursor=next?null:"next-page"};
   }
   if(method=="thread/turns/list") {
    var tid=p.GetProperty("threadId").GetString(); var cursor=p.GetProperty("cursor");
    if(tid=="delayed-history" || forkCache && tid=="fork-history")await Task.Delay(300);
    if(forkNoise && tid=="fork-history") {await Emit(new {id="stale-fork-request",method="item/commandExecution/requestApproval",@params=new {threadId="unrelated-old-thread",command="unrelated"}});await Task.Delay(100);}
    if(tid=="unavailable-history") {await Emit(new {id,error=new {code=-32000,message="Storage temporarily unavailable"}});continue;}
    if(tid=="legacy-history") {await Emit(new {id,error=new {code=-32601,message="Unknown method"}});continue;}
    var text=tid=="paged-history"?(cursor.ValueKind==JsonValueKind.Null?"Newer":"Older"):"Original CLI conversation";
    object[] historyTurns=tid=="thread-main"?[]:[new {id="history-turn",items=new[]{new {id=text,type="agentMessage",text}}}];
    result=new {data=historyTurns,nextCursor=tid=="paged-history" && cursor.ValueKind==JsonValueKind.Null?"older-page":null};
   }
   if(method=="thread/read")result=new {thread=new {id="legacy-history",cwd=Path.GetTempPath(),turns=new[]{new {id="legacy-turn",items=new[]{new {id="legacy-message",type="agentMessage",text="Legacy canonical"}}}}}};
   if(method=="review/start" || method=="thread/compact/start") {
    if(p.GetProperty("threadId").GetString()!=currentThread)throw new Exception("Wrong manual thread");
    result=method=="review/start"?new {turn=new {id="manual-turn",status="inProgress"}}:(object)new {};
    await Emit(new {id,result});
    await Event("turn/started",new {threadId=currentThread,turn=new {id="manual-turn"}});
    await Event("turn/completed",new {threadId=currentThread,turn=new {id="manual-turn",status="completed"}});continue;
   }
   if(method=="model/list")result=new {data=Array.Empty<object>()};
   if(method=="account/read")result=new {account=new {type="chatgpt",email="test@example.invalid",planType="plus"}};
   if(method=="account/rateLimits/read") {if(slow)await Task.Delay(200); result=new {rateLimits=new {primary=new {usedPercent=10,resetsAt=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}};}
   if(method=="turn/steer")
   {
    if(p.GetProperty("threadId").GetString()!=currentThread || p.GetProperty("expectedTurnId").GetString()!="turn-1" || p.TryGetProperty("model",out _))throw new Exception("Invalid steering target or override");
    var steeringText=p.GetProperty("input")[0].GetProperty("text").GetString()!;
    if(steeringText.Contains("reject steer")) {await Emit(new {id,error=new {code=-32000,message="Steering rejected"}});continue;}
    await Emit(new {id,result=new {turnId="turn-1"}});continue;
   }
   if(method=="turn/start")
   {
    if(!loaded){await Emit(new {id,error=new {code=-32600,message="Thread is not loaded"}});continue;}
    turns++; var text=p.GetProperty("input")[0].GetProperty("text").GetString()!; slow=text.Contains("slow");
    result=new {turn=new {id="turn-"+turns,status="inProgress"}};
    await Emit(new {id,result});
    await Event("turn/started",new {threadId=currentThread,turn=new {id="turn-"+turns}});
    if(text.Contains("keep working"))continue;
    if(text.Contains("unknown approval")) {await Emit(new {id="unknown-99",method="item/strange/requestApproval",@params=new {threadId=currentThread,turnId="turn-"+turns}});continue;}
    if(text.Contains("quota") && turns==1) {await Event("turn/completed",new {threadId=currentThread,turn=new {id="turn-1",status="failed",error=new {message="Usage limit reached",codexErrorInfo="usageLimitExceeded"}}});continue;}
    await Event("item/agentMessage/delta",new {threadId=currentThread,itemId="message-"+turns,delta="Resposta"});
    await Event("item/completed",new {threadId=currentThread,item=new {id="message-"+turns,type="agentMessage",text="Resposta final"}});
    await Event("turn/completed",new {threadId=currentThread,turn=new {id="turn-"+turns,status="completed"}});continue;
   }
   await Emit(new {id,result});
  }
 }
}
