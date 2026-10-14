using System.Diagnostics;
using NightShift.App;
using NightShift.Core;

internal static class RecentTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Portable upgrade imports previous recents without overwriting new data", async () =>
        {
            var root=Path.Combine(Path.GetTempPath(),"ns-upgrade-"+Guid.NewGuid());
            var previous=new AppStore(Path.Combine(root,"previous"));
            var current=new AppStore(Path.Combine(root,"current"));
            try
            {
                previous.SaveSettings(new AppSettings {ProjectPath=root,ShiftHours=12});
                previous.SaveSession(new SessionRecord {ThreadId="old-recent",Title="Previous session",ProjectPath=root});
                PortableDataMigration.ImportMissing(current.Root,previous.Root);
                if(current.LoadSettings().ShiftHours!=12 || current.LoadSessions().Single().ThreadId!="old-recent")
                    throw new Exception("Portable upgrade lost settings or recent metadata");
                current.SaveSettings(new AppSettings {ShiftHours=3});
                PortableDataMigration.ImportMissing(current.Root,previous.Root);
                if(current.LoadSettings().ShiftHours!=3)throw new Exception("Upgrade overwrote new settings");
            }
            finally {Directory.Delete(root,true);}
            await Task.CompletedTask;
        });
        yield return ("Recent conversations survive a scoped History refresh", async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"ns-recent-"+Guid.NewGuid());
            var store=new AppStore(folder);
            store.SaveSession(new SessionRecord {ThreadId="cached-other",ProjectPath=folder,Title="Cached project",UpdatedAt=DateTimeOffset.UtcNow});
            var server=new ProcessStartInfo(Environment.ProcessPath!);
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))server.ArgumentList.Add(typeof(RecentTests).Assembly.Location);
            server.ArgumentList.Add("--fake-server");
            await using var session=new CodexSession(); await session.ConnectAsync(server,new());
            await using var view=new MainViewModel(store,session);
            try
            {
                view.ProjectPath=Path.GetTempPath();
                await view.RefreshHistoryAsync();
                if(view.Sessions.Count!=1 || view.Sessions[0].ThreadId!="cli-history")throw new Exception("History fixture did not replace its scoped page");
                if(!view.RecentSessions.Any(s=>s.ThreadId=="cached-other") || !view.RecentSessions.Any(s=>s.ThreadId=="cli-history"))
                    throw new Exception("History refresh erased another project's recent conversation");
                await view.RefreshRecentAsync();
                if(view.RecentSessions.Count(s=>s.ThreadId=="cli-history")!=1)throw new Exception("Recent refresh duplicated a canonical conversation");
                view.ProjectPath=folder; await view.RefreshRecentAsync();
                if(!SessionCatalog.SameProject(view.RecentSessions.Single(s=>s.ThreadId=="cli-history").ProjectPath,folder))
                    throw new Exception("Recent refresh ignored the selected project");
            }
            finally {await view.DisposeAsync();Directory.Delete(folder,true);}
        });
    }
}
