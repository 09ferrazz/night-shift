using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NightShift.App;
using NightShift.Core;

internal static partial class Program
{
    private static async Task TestSlimAsync(MainWindow window, MainViewModel view, string output)
    {
        var method = typeof(MainWindow).GetMethod("SlimClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(window, [window, new RoutedEventArgs()]);
        await Settle(window);
        var slim = (SlimWindow)typeof(MainWindow).GetField("_slim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(!window.IsVisible && slim.IsVisible, "Slim must hide the full app");
        Check(ReferenceEquals(slim.DataContext, view), "Slim created another view model or session");
        Check(slim.Width == SlimPlacement.CatSize && slim.Height == SlimPlacement.CatSize && !slim.ShowActivated, "Collapsed cat must remain compact without forcing focus");
        Check(slim.Topmost && !slim.ShowInTaskbar && slim.WindowStyle == WindowStyle.None && slim.AllowsTransparency,
            "Floating cat must be transparent, topmost and without window chrome");
        var cat = (Button)slim.FindName("CatButton");
        Check(cat.Content is Image, "Floating cat must preserve the approved image asset");
        var catPosition = new Point(slim.Left, slim.Top);
        cat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(slim);
        Check(slim.IsPanelOpen && slim.Width <= 340, "Cat did not open the compact panel");
        Check(((FrameworkElement)slim.FindName("CompactPanel")).IsVisible, "Compact panel is hidden");
        Check(slim.FindName("CodexProvider") is Button && slim.FindName("ClaudeProvider") is Button,
            "Slim must offer both labeled providers");
        Check(slim.FindName("LiveList") is ListBox && slim.FindName("PendingButton") is Button,
            "Slim is missing live sessions or permission access");
        Check(ReferenceEquals(slim.DataContext, view), "Expanding the panel replaced the session");
        Capture(slim, System.IO.Path.Combine(output, "slim-panel.png"));
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(slim), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        slim.RaiseEvent(escape); await Settle(slim);
        Check(escape.Handled, "Esc did not close the slim panel");
        Check(new Point(slim.Left, slim.Top) == catPosition, "Opening a panel changed the persisted cat anchor");
        Check(!slim.IsPanelOpen && slim.Width == SlimPlacement.CatSize && slim.Height == SlimPlacement.CatSize, "Closing panel changed cat size");
        slim.Close(); await Settle(window);
        Check(window.IsVisible, "Closing slim must return to the full app");
        Check(ReferenceEquals(window.DataContext, view), "Returning from slim replaced the session");
        Check(view.CanConnect, "Closing slim disposed the shared view model");
        Console.WriteLine("PASS slim shared-model lifecycle and compact panel");
    }
}
