using System.Windows;
namespace NightShift.App;
public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, NightShift.Core.AppStore.InstanceName, out var created);
        if (!created) { MessageBox.Show("O Night Shift já está aberto. Verifique a barra de tarefas.", "Night Shift"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show("Não foi possível completar a ação.\n" + args.Exception.Message, "Night Shift"); args.Handled = true; };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
