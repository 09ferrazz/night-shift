using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using NightShift.App;
using NightShift.Core;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "nightshift.portable"), "isolated UI validation");
        var errors = new StringWriter(); PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { errors.WriteLine(e.Exception); e.Handled = true; };
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Theme.xaml"));
        var dictionary = new XElement(wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:NightShift.App;assembly=NightShift"), theme.Root!.Element(wpf + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        int exit = 1;
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0 };
                window.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var view = (MainViewModel)window.DataContext;
                view.Platform = CodexPlatform.Cli;
                view.ProjectPath = output;
                await TestAppearanceAsync(window, view);
                await TestCodeBlockAsync(window, view);
                await TestLongMessageAsync(window, view);
                await TestHybridScenariosAsync(window, view, output);
                await TestAttachmentsAndFeedbackAsync(window, view, output);
                await TestSlimAsync(window, view, output);
                window.Width = 1120; window.Height = 780; await Settle(window);
                var composer = (TextBox)window.FindName("Composer");
                var menu = (Border)window.FindName("NavigationPanel");
                Check(menu.Visibility == Visibility.Visible, "Conversation sidebar must be visible on a wide window");
                foreach (var size in new[] { (1120, 780), (800, 600), (760, 560) })
                {
                    window.Width = size.Item1; window.Height = size.Item2;
                    await Settle(window);
                    var point = composer.TransformToAncestor(window).Transform(new Point());
                    Check(composer.ActualHeight >= 56 && point.Y >= 0 && point.Y + composer.ActualHeight < window.ActualHeight - 45, "Composer clipped at " + size);
                    Capture(window, Path.Combine(output, $"conversa-{size.Item1}x{size.Item2}.png"));
                    Console.WriteLine("PASS WPF layout " + size);
                }
                window.Width = 1120; window.Height = 780;
                view.Platform = CodexPlatform.Desktop;
                foreach (var dark in new[] { false, true })
                {
                    view.IsDarkTheme = dark;
                    foreach (var page in new[] { "Settings", "Integrations", "Live", "History", "Commands", "Activity", "Session" })
                    {
                        typeof(MainWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [page]);
                        await Settle(window); Capture(window, Path.Combine(output, $"{page.ToLowerInvariant()}-{(dark ? "dark" : "light")}.png"));
                        Console.WriteLine($"PASS WPF page {page} ({(dark ? "dark" : "light")})");
                    }
                }
                view.Messages.Add(new DisplayMessage { Id = "render-test", Role = "Codex", Text = "Validação visual com conteúdo simulado.\n\nA conversa fica em primeiro plano, com português e Unicode: ação, permissão, 🌙.\n\n" + new string('x', 500) });
                view.Draft = "Mensagem de teste; nenhum pedido enviado ao Codex.";
                await Settle(window); Capture(window, Path.Combine(output, "conversa-conteudo.png"));
                ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(1.25, 1.25);
                await Settle(window); Capture(window, Path.Combine(output, "escala-125.png"));
                Check(string.IsNullOrWhiteSpace(errors.ToString()), "WPF binding warnings: " + errors);
                Console.WriteLine("PASS no WPF binding errors; native Windows rendering completed");
                // Exercise synchronous disposal too: Close must never run recursively
                // inside the first Closing event, even if disposal has already finished.
                await view.DisposeAsync();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(string.IsNullOrWhiteSpace(errors.ToString()), "Window closing errors: " + errors);
                Console.WriteLine("PASS WPF window closes after synchronous disposal"); exit = 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); }
            finally
            {
                if (window?.DataContext is MainViewModel view) await view.DisposeAsync();
                app.Shutdown(exit);
            }
        };
        app.Run(); return exit;
    }
    private static async Task Settle(Window window)
    { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static async Task TestAppearanceAsync(MainWindow window, MainViewModel view)
    {
        var toggle = window.FindName("ThemeToggle") as CheckBox;
        Check(toggle is not null, "Approved light/dark theme toggle is missing");
        var draft = "Rascunho preservado ao alternar tema"; view.Draft = draft;
        toggle!.IsChecked = false; await Settle(window);
        var light = ((SolidColorBrush)window.Background).Color;
        Check(light.R > 220 && light.G > 220, "Light theme must update real window resources");
        Check(view.Draft == draft, "Theme switching lost the draft");
        var reloaded = new MainViewModel();
        try { Check((bool)typeof(MainViewModel).GetProperty("IsDarkTheme")!.GetValue(reloaded)! == false, "Theme preference was not saved"); }
        finally { await reloaded.DisposeAsync(); }
        toggle.IsChecked = true; await Settle(window);
        var dark = ((SolidColorBrush)window.Background).Color;
        Check(dark.R < 50 && dark.G < 60, "Dark theme must update real window resources");
        Check(window.FindName("PrimaryAction") is Button, "Single primary send/stop button is missing");
        view.Draft = "";
        Console.WriteLine("PASS live theme switching, persistence and draft preservation");
    }
    private static async Task TestCodeBlockAsync(MainWindow window, MainViewModel view)
    {
        const string code = "VirtualizingPanel.ScrollUnit=\"Pixel\"";
        var message = new DisplayMessage { Id = "code", Text = "Antes do código.\n\n```xaml\n" + code + "\n```\nDepois do código." };
        view.Messages.Add(message); await Settle(window);
        var list = (ListBox)window.FindName("ChatList");
        var item = (DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check(Children<TextBox>(item).Any(t => t.Text == code), "Fenced code must have a separate selectable code block");
        Check(Children<TextBox>(item).Any(t => t.Text.Contains("Depois do código.")), "Code rendering lost the trailing prose");
        message.Text = "```xaml\n" + code + "\nEM_STREAMING"; await Settle(window);
        Check(Children<TextBox>(item).Any(t => t.Text.Contains("EM_STREAMING")), "Incomplete code fence hides streaming content");
        view.Messages.Clear(); await Settle(window);
        Console.WriteLine("PASS native selectable code blocks and unfinished streaming fences");
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root,i);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private static T? Child<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var item = VisualTreeHelper.GetChild(root, i);
            if (item is T match) return match;
            if (Child<T>(item) is { } nested) return nested;
        }
        return null;
    }
    private static async Task TestLongMessageAsync(MainWindow window, MainViewModel view)
    {
        var list = (ListBox)window.FindName("ChatList");
        var message = new DisplayMessage { Id = "long-scroll", Text = string.Concat(Enumerable.Repeat("Linha longa com conteúdo legível.\n", 180)) + "FIM_VISIVEL" };
        view.Messages.Add(message); await Settle(window);
        var scroll = Child<ScrollViewer>(list)!;
        Check(scroll.ScrollableHeight > 1000, "One tall message must have a pixel scroll range");
        scroll.ScrollToEnd(); await Settle(window);
        Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 2, "ScrollToEnd cannot reach the tall message tail");
        var text = Child<TextBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0))!;
        var tail = text.GetRectFromCharacterIndex(text.Text.Length - 1);
        var end = text.TransformToAncestor(scroll).Transform(new Point(tail.Right, tail.Bottom));
        Check(end.Y <= scroll.ActualHeight && end.Y >= 0, "Message tail is outside the viewport");
        scroll.ScrollToVerticalOffset(500); await Settle(window);
        var offset = scroll.VerticalOffset;
        text.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        await Settle(window);
        Check(scroll.VerticalOffset > offset, "Mouse wheel over message text failed to scroll the conversation");
        offset = scroll.VerticalOffset;
        var bar = Children<ScrollBar>(list).First(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        var thumb = Children<Thumb>(bar).Single();
        thumb.RaiseEvent(new DragStartedEventArgs(0,0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0,20) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0,20,false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Settle(window);
        Check(scroll.VerticalOffset > offset, "Dragging the themed scrollbar did not move the conversation");
        offset = scroll.VerticalOffset;
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.PageDown) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Settle(window);
        Check(scroll.VerticalOffset > offset, "PageDown did not scroll the conversation");
        offset = scroll.VerticalOffset;
        message.Text += string.Concat(Enumerable.Repeat("Novo conteúdo recebido.\n", 40));
        await Settle(window);
        Check(Math.Abs(scroll.VerticalOffset - offset) < 2, "Streaming moved the reading position");
        Check((bool)typeof(MainWindow).GetField("_autoScroll", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! == false, "Autoscroll remains armed while reading above the bottom");
        window.Width = 800; window.Height = 600; await Settle(window);
        scroll.ScrollToEnd(); await Settle(window);
        Check(scroll.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 2, "Resize lost access to the end");
        message.Text = string.Concat(Enumerable.Repeat("Uma linha completa e preservada.\n", 6500)) + "FIM_200_MIL";
        await Settle(window);
        scroll.ScrollToEnd(); await Settle(window);
        Check(message.Text.Length > 200000 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 2, "200k response tail unreachable");
        text = Child<TextBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0))!;
        tail = text.GetRectFromCharacterIndex(text.Text.Length - 1);
        end = text.TransformToAncestor(scroll).Transform(new Point(tail.Right, tail.Bottom));
        Check(end.Y <= scroll.ActualHeight && end.Y >= 0, "200k final line is clipped");
        view.Messages.Clear(); await Settle(window);
        Console.WriteLine("PASS WPF tall message, readable tail, reading position and resize");
    }
    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = content.TransformToAncestor(window).TransformBounds(new Rect(content.RenderSize));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
