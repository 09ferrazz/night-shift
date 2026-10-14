using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightShift.Core;
internal static class ProviderStoreTests
{
    public static IEnumerable<(string,Func<Task>)> Cases()
    {
        yield return ("Provider cache isolates identical IDs and preserves old Codex hashes",()=> {
            var root=Path.Combine(Path.GetTempPath(),"providers-"+Guid.NewGuid());
            try {
                var store=new AppStore(root);
                store.SaveSession(new SessionRecord {ThreadId="same",Title="Codex"});
                store.SaveSession(new SessionRecord {ThreadId="claude:same",Title="Prefixed Codex"});
                store.SaveSession(new SessionRecord {ThreadId="same",Provider=AgentProvider.Claude,Title="Claude",ExternalId="worker",ExternalKind="background"});
                if(store.LoadSession("claude:same")!.Title!="Prefixed Codex")throw new Exception("Cross provider prefixed ID collision");
                if(store.LoadSession("same")!.Title!="Codex" || store.LoadSession("same",AgentProvider.Claude)!.Title!="Claude")throw new Exception("Provider collision");
                if(!File.Exists(Path.Combine(root,"sessions",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("same")))+".json")))throw new Exception("Old Codex hash changed");
                if(store.LoadSessions().Single(r=>r.Provider==AgentProvider.Claude).ExternalKind!="background")throw new Exception("Metadata lost provider or origin");
                if(JsonSerializer.Deserialize<AppSettings>("{}")!.Provider!=AgentProvider.Codex)throw new Exception("Old settings changed provider");
            }finally{Directory.Delete(root,true);}return Task.CompletedTask;
        });
    }
}
