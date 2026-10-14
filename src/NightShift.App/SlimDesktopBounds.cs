using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NightShift.Core;

namespace NightShift.App;

internal static class SlimDesktopBounds
{
    private const uint MonitorDefaultToNearest = 2;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public uint Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    public static SlimWorkArea InitialArea(Window window, double? left, double? top)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var fallback = SystemParameters.WorkArea;
        var x = left is { } l && double.IsFinite(l) ? l : fallback.Right - SlimPlacement.CatSize;
        var y = top is { } t && double.IsFinite(t) ? t : fallback.Bottom - SlimPlacement.CatSize;
        var point = new NativePoint { X = ToPixel(x * dpi.DpiScaleX), Y = ToPixel(y * dpi.DpiScaleY) };
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        // Before a source exists, use the destination monitor's scale. WPF's per-monitor
        // source subsequently supplies the authoritative logical transform.
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dx, out var dy) == 0)
                return ReadArea(monitor, dx / 96.0, dy / 96.0);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return ReadArea(monitor, dpi.DpiScaleX, dpi.DpiScaleY);
    }
    public static SlimWorkArea CurrentArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return InitialArea(window, window.Left, window.Top);
        var dpi = VisualTreeHelper.GetDpi(window);
        return ReadArea(MonitorFromWindow(handle, MonitorDefaultToNearest), dpi.DpiScaleX, dpi.DpiScaleY);
    }
    private static SlimWorkArea ReadArea(nint monitor, double scaleX, double scaleY)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
            return new(info.Work.Left / scaleX, info.Work.Top / scaleY,
                (info.Work.Right - info.Work.Left) / scaleX, (info.Work.Bottom - info.Work.Top) / scaleY);
        var fallback = SystemParameters.WorkArea;
        return new(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }
    private static int ToPixel(double value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
