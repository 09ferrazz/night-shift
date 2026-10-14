namespace NightShift.Core;

public readonly record struct SlimPoint(double Left, double Top);
public readonly record struct SlimWorkArea(double Left, double Top, double Width, double Height);

/// <summary>Logical desktop coordinates, including monitors to the left of the primary display.</summary>
public static class SlimPlacement
{
    public const double CatSize = 64;
    public const double PanelWidth = 340;
    public const double ExpandedHeight = 640;

    public static SlimPoint Clamp(double? left, double? top, double width, double height, SlimWorkArea area)
    {
        if (!double.IsFinite(area.Left) || !double.IsFinite(area.Top) || !double.IsFinite(area.Width)
            || !double.IsFinite(area.Height) || area.Width <= 0 || area.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(area), "Work area must be finite and positive.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Window size must be finite and positive.");
        var maxLeft = area.Left + Math.Max(0, area.Width - width);
        var maxTop = area.Top + Math.Max(0, area.Height - height);
        var x = left is { } l && double.IsFinite(l) ? l : maxLeft - 24;
        var y = top is { } t && double.IsFinite(t) ? t : maxTop - 24;
        return new(Math.Clamp(x, area.Left, maxLeft), Math.Clamp(y, area.Top, maxTop));
    }
}
