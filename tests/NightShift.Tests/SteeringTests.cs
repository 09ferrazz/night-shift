using System.Diagnostics;
using NightShift.App;
using NightShift.Core;

internal static class SteeringTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Sending during active turn steers without stopping Night Shift", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ns-steer-" + Guid.NewGuid());
            await using var session = new CodexSession();
            var server = new ProcessStartInfo(Environment.ProcessPath!);
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                server.ArgumentList.Add(typeof(SteeringTests).Assembly.Location);
            server.ArgumentList.Add("--fake-server");
            await session.ConnectAsync(server, new());
            await using var view = new MainViewModel(new AppStore(folder), session);
            try
            {
                var messages = new List<ChatEntry>(); session.ChatChanged += messages.Add;
                view.ProjectPath = Path.GetTempPath(); view.Draft = "keep working";
                await view.SetNightShiftAsync(true); await view.SendPrimaryAsync();
                if (!session.IsBusy || !session.Shift.Armed) throw new Exception("Fixture did not hold an armed turn");
                view.Draft = "Refine the active answer";
                if (!view.CanSend || view.PrimaryActionLabel != "Enviar") throw new Exception("Composer still blocks active-turn input");
                await view.SendPrimaryAsync();
                if (view.Draft != "" || !session.IsBusy || !session.Shift.Armed || session.TurnsStarted != 1)
                    throw new Exception("Steering interrupted, duplicated or disarmed the active turn");
                if (messages.Count(m => m.Role == "Você" && m.Text == "Refine the active answer") != 1)
                    throw new Exception("Accepted steer was not shown once in the conversation");
                view.Draft = "reject steer";
                try { await view.SendAsync(); throw new Exception("Rejected steer was treated as accepted"); }
                catch (RpcException) { }
                if (view.Draft != "reject steer" || !session.Shift.Armed || !session.IsBusy)
                    throw new Exception("Rejected steer lost the draft or stopped the active turn");
                await view.StopAsync();
            }
            finally { await view.DisposeAsync(); Directory.Delete(folder, true); }
        });
        yield return ("Pending approval keeps the message draft until the request is resolved", async () =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"ns-steer-pending-"+Guid.NewGuid());
            var server=new ProcessStartInfo(Environment.ProcessPath!);
            if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))
                server.ArgumentList.Add(typeof(SteeringTests).Assembly.Location);
            server.ArgumentList.Add("--fake-server");
            await using var session=new CodexSession(); await session.ConnectAsync(server,new());
            await using var view=new MainViewModel(new AppStore(folder),session);
            try
            {
                view.ProjectPath=Path.GetTempPath(); view.Draft="unknown approval"; await view.SendAsync();
                var deadline=DateTime.UtcNow.AddSeconds(3);
                while(session.PendingRequests.Count==0 && DateTime.UtcNow<deadline)await Task.Delay(10);
                if(session.PendingRequests.Count==0)throw new Exception("Fixture did not reach approval state");
                view.Draft="Follow-up after approval";
                if(view.CanSend || !view.CanStop || !session.IsBusy)throw new Exception("Approval state allowed steering or hid Stop");
                await view.StopAsync();
                if(view.Draft!="Follow-up after approval")throw new Exception("Approval stop erased the draft");
            }
            finally {await view.DisposeAsync();Directory.Delete(folder,true);}
        });
    }
}
