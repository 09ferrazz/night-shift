// The real view model runs on Linux with inert timer/native hosts. This exercises
// persistence and command behavior, not WPF rendering or Dispatcher scheduling.
namespace System.Windows.Threading
{
    internal sealed class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler? Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
    }
}
namespace System.Windows.Media { internal sealed class UiTestNamespace { } }
namespace NightShift.App { internal static class NativeMethods { public static void SetAwake(bool value) { } } }
