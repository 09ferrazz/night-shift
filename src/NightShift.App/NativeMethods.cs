using System.Runtime.InteropServices;
namespace NightShift.App;
internal static class NativeMethods
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void SetAwake(bool awake) => SetThreadExecutionState(0x80000000u | (awake ? 0x00000001u : 0));
    public static void DarkTitlebar(IntPtr window, bool dark = true) { var enabled = dark ? 1 : 0; DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int)); }
}
