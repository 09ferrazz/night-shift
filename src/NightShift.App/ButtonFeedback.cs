using System.Windows;
using System.Windows.Media;

namespace NightShift.App;

public static class ButtonFeedback
{
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(ButtonFeedback));
    public static readonly DependencyProperty PressedBrushProperty = DependencyProperty.RegisterAttached("PressedBrush", typeof(Brush), typeof(ButtonFeedback));
    public static Brush GetHoverBrush(DependencyObject item) => (Brush)item.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject item, Brush value) => item.SetValue(HoverBrushProperty, value);
    public static Brush GetPressedBrush(DependencyObject item) => (Brush)item.GetValue(PressedBrushProperty);
    public static void SetPressedBrush(DependencyObject item, Brush value) => item.SetValue(PressedBrushProperty, value);
}
