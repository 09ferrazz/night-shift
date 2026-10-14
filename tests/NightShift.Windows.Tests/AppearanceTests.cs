using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NightShift.App;
using NightShift.Core;

internal static partial class Program
{
    private static async Task TestHybridScenariosAsync(MainWindow window, MainViewModel view, string output)
    {
        Check(window.FindName("LimitsButton") is Button && window.FindName("QuotaPanel") is Border, "Compact quota panel is missing");
        var toggle = (CheckBox)window.FindName("ThemeToggle");
        var root = (FrameworkElement)window.Content;
        var primary = (Button)window.FindName("PrimaryAction");
        var recent = (ListBox)window.FindName("RecentList");
        var search = (TextBox)window.FindName("SidebarSearch");
        var project = view.ProjectPath;
        var previewScroll=new SessionRecord { ThreadId = "preview-scroll", Title = "Melhorar a leitura da conversa", ProjectPath = project };
        var previewHistory=new SessionRecord { ThreadId = "preview-history", Title = "Organizar o histórico", ProjectPath = project };
        var otherProject=new SessionRecord { ThreadId = "other-project", Title = "Conversa em outro projeto", ProjectPath = "C:\\unrelated-project" };
        foreach(var record in new[]{previewScroll,previewHistory,otherProject}) { view.Sessions.Add(record); view.RecentSessions.Add(record); }
        await Settle(window); Check(recent.Items.Count == 2,"Recent sidebar ignored the project filter");
        var equivalentPath=new SessionRecord { ThreadId="same-project-alias", Title="Conversa antiga", ProjectPath=System.IO.Path.Combine(project,".") };
        view.RecentSessions.Add(equivalentPath); await Settle(window);
        Check(recent.Items.Count==3,"Recent sidebar hid an equivalent project path");
        view.RecentSessions.Remove(equivalentPath);
        search.Text = "histórico"; await Settle(window);
        Check(recent.Items.Count == 1 && view.Sessions.Count == 3,"Sidebar search changed the original history collection");
        window.Width=1280; window.Height=820; await Settle(window);
        search.Focus(); search.Text="aqui está bugado"; await Settle(window);
        var textHost=Children<ScrollViewer>(search).FirstOrDefault();
        Check(textHost is not null && textHost.ActualHeight > 0,"Search text host is missing");
        var character = search.GetRectFromCharacterIndex(0);
        Check(!character.IsEmpty && character.Top >= 0 && character.Bottom <= search.ActualHeight,
            "Search clips the typed character");
        Check(Math.Abs(character.Top + character.Height / 2 - search.ActualHeight / 2) <= 3,
            "Typed search text is not vertically centered");
        Capture(window,System.IO.Path.Combine(output,"search-visible.png"));
        view.IsDarkTheme=false; await Settle(window);
        Capture(window,System.IO.Path.Combine(output,"search-visible-light.png"));
        view.IsDarkTheme=true; await Settle(window);
        search.Text = "";
        var sidebar=(Border)window.FindName("NavigationPanel");
        var activityNav=Children<Button>(sidebar).Single(b=>b.Tag as string=="Activity");
        var settingsNav=Children<Button>(sidebar).Single(b=>b.Tag as string=="Settings");
        var activityBottom=activityNav.TransformToAncestor(sidebar).Transform(new Point(0,activityNav.ActualHeight)).Y;
        var settingsTop=settingsNav.TransformToAncestor(sidebar).Transform(new Point()).Y;
        Check(settingsTop-activityBottom>=6,"Activity and Settings buttons have no breathing room");
        typeof(MainWindow).GetMethod("ShowPage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,["Settings"]); await Settle(window);
        var back=(Button?)window.FindName("BackButton");
        Check(back is not null && back.IsVisible,"Back button is missing on secondary pages");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Check(((Grid)window.FindName("SessionPage")).IsVisible,"Back did not return to the conversation");
        ((Button)window.FindName("LimitsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Check(back.IsVisible,"Back button is missing when quota overlay is open");
        back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Check(!((Border)window.FindName("QuotaPanel")).IsVisible && ((Grid)window.FindName("SessionPage")).IsVisible,"Back did not close limits in place");
        view.Messages.Add(new() { Id = "preview-user", Role = "Você", Text = "Revise a rolagem e preserve as respostas completas." });
        view.Messages.Add(new() { Id = "preview-assistant", Text = "Prévia com conteúdo simulado para conferir o layout.\n\n1. Rolagem por pixels\n2. Texto integral no histórico\n\n```xaml\nVirtualizingPanel.ScrollUnit=\"Pixel\"\n```" });
        typeof(MainViewModel).GetField("_sessionTitle", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view,"Melhorar a leitura da conversa");
        view.Draft = "";
        foreach (var dark in new[] { false,true })
        {
            toggle.IsChecked = dark;
            foreach (var size in new[] { (1280,820),(1120,780),(800,600),(760,560) })
            {
                window.Width = size.Item1; window.Height = size.Item2; await Settle(window);
                Inside(root,primary,"Primary action clipped in theme/size " + dark + size);
                Inside(root,toggle,"Theme toggle clipped in theme/size " + dark + size);
                Inside(root,(FrameworkElement)window.FindName("NightToggle"),"Night toggle clipped");
                Capture(window,System.IO.Path.Combine(output,$"hybrid-{(dark ? "dark" : "light")}-{size.Item1}.png"));
                var panel = (Border)window.FindName("QuotaPanel");
                var limits = (Button)window.FindName("LimitsButton");
                var before = primary.TransformToAncestor(root).Transform(new Point());
                limits.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
                Check(panel.IsVisible && panel.ActualWidth <= 310 && panel.ActualHeight <= 260, "Quota panel is not compact");
                Inside(root, panel, "Quota overlay clipped at " + size);
                Check(primary.TransformToAncestor(root).Transform(new Point()) == before, "Quota overlay moved the composer");
                Check(Children<ProgressBar>(panel).All(b => !b.IsVisible), "Unknown limits displayed invented bars");
                Capture(window,System.IO.Path.Combine(output,$"limits-unknown-{(dark ? "dark" : "light")}-{size.Item1}.png"));
                limits.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
                Check(!panel.IsVisible, "Clicking limits again did not close the overlay");
            }
            await TestLongMessageInThemeAsync(window,view,dark);
        }
        window.Width=1280; window.Height=820;
        foreach(var scale in new[] {1.25,1.5})
        {
            root.LayoutTransform=new ScaleTransform(scale,scale); await Settle(window);
            Inside(root,primary,"Scaled primary action is inaccessible"); Inside(root,toggle,"Scaled header is inaccessible");
            Capture(window,System.IO.Path.Combine(output,$"hybrid-scale-{scale*100:0}.png"));
        }
        root.LayoutTransform=Transform.Identity;
        window.Width=760; window.Height=560; await Settle(window);
        sidebar=(Border)window.FindName("NavigationPanel");
        Check(!sidebar.IsVisible,"Small windows must initially collapse the sidebar");
        ((Button)window.FindName("OpenSidebar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Check(sidebar.IsVisible,"Sidebar cannot be reopened at minimum width");
        Inside(root,primary,"Opening the sidebar pushed Parar outside the window");
        ((Button)window.FindName("OpenSidebar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var session=(CodexSession)typeof(MainViewModel).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(CodexSession).GetProperty("Rates")!.SetValue(session, RateSnapshot.Parse(JsonSerializer.SerializeToElement(new {
            rateLimits = new { primary = new { usedPercent = 73.5, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() }, secondary = new { usedPercent = 42, windowDurationMins = 10080, resetsAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() } }
        })));
        foreach (var dark in new[] { false, true })
        {
            toggle.IsChecked = dark; view.Draft = "Prévia com dados de limite simulados"; await Settle(window);
            ((Button)window.FindName("LimitsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
            var panel = (Border)window.FindName("QuotaPanel");
            Inside(root, panel, "Known limits clipped on small window");
            Check(Children<ProgressBar>(panel).Select(b => b.Value).SequenceEqual(new[] { 73.5, 42.0 }), "Quota bars show incorrect usage");
            Capture(window,System.IO.Path.Combine(output,$"limits-{(dark ? "dark" : "light")}.png"));
            var source = System.Windows.PresentationSource.FromVisual(window)!;
            var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape); await Settle(window);
            Check(escape.Handled && !panel.IsVisible && view.Draft.Length > 0, "Escape did not dismiss quota overlay safely");
        }
        var pending=(List<PendingRequest>)typeof(CodexSession).GetField("_pending",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
        pending.Add(new(JsonSerializer.SerializeToElement(123),"item/commandExecution/requestApproval",JsonSerializer.SerializeToElement(new { command="comando ilustrativo; não executar" })));
        session.Shift.Arm(DateTimeOffset.UtcNow,1); session.Shift.WaitForQuota(DateTimeOffset.UtcNow.AddMinutes(20));
        view.Notice=string.Concat(Enumerable.Repeat("Aviso longo de teste para conferir o acesso aos controles.\n",30));
        view.Draft="Rascunho"; await Settle(window);
        var stop=(Button)window.FindName("ComposerStop");
        Check((string)primary.Content == "Enviar" && stop.IsVisible && stop.IsEnabled,"Pending/quota state hides or disables Parar");
        Inside(root,stop,"Notices pushed Parar outside window");
        Inside(root,(FrameworkElement)window.FindName("NightToggle"),"Combined notices pushed the night toggle outside the window");
        var answer=Children<Button>(root).Single(b=>b.Content as string == "Responder");
        Check(answer.IsVisible,"Pending request response action is hidden"); Inside(root,answer,"Pending response clipped");
        Capture(window,System.IO.Path.Combine(output,"hybrid-pending-quota-small.png"));
        var pendingPrimaryPoint = primary.TransformToAncestor(root).Transform(new Point());
        ((Button)window.FindName("LimitsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Capture(window,System.IO.Path.Combine(output,"pending-quota-overlay.png"));
        Inside(root,(FrameworkElement)window.FindName("QuotaPanel"),"Quota overlay clipped with combined notices");
        Inside(root,(FrameworkElement)window.FindName("NightToggle"),"Quota overlay pushed night toggle outside window");
        Check(primary.TransformToAncestor(root).Transform(new Point()) == pendingPrimaryPoint,"Quota overlay shifted composer during pending request");
        var pendingEscape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(pendingEscape); await Settle(window);
        Check(!((Border)window.FindName("QuotaPanel")).IsVisible && session.Shift.Armed && session.PendingRequests.Count == 1,"Dismissing limits interrupted the pending turn");
        pending.Clear(); session.Shift.Stop(); view.Notice=""; view.Draft="";
        foreach(var dark in new[] {false,true})
        {
            toggle.IsChecked=dark;
            var request=new PendingRequest(JsonSerializer.SerializeToElement("form"),"item/tool/requestUserInput",JsonSerializer.SerializeToElement(new { questions=new[] {new {id="secret",question="Pergunta ilustrativa para validar contraste",isSecret=true}} }));
            var dialog=new RequestWindow(request) {ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-30000,Top=0};
            dialog.Show(); await Settle(dialog);
            var password=Children<PasswordBox>(dialog).Single();
            var fg=((SolidColorBrush)password.Foreground).Color; var bg=((SolidColorBrush)password.Background).Color;
            Check(Math.Abs(fg.R-bg.R)>90,"Permission input text has unreadable theme contrast");
            Capture(dialog,System.IO.Path.Combine(output,$"permission-{(dark?"dark":"light")}.png")); dialog.Close();
        }
        view.Messages.Clear(); view.Sessions.Clear(); view.RecentSessions.Clear(); view.Draft=""; window.Width=1120; window.Height=780; await Settle(window);
        Console.WriteLine("PASS hybrid themes, project search, navigation, 125/150% layout, pending/quota and permission forms (simulated states)");
    }
    private static async Task TestLongMessageInThemeAsync(MainWindow window,MainViewModel view,bool dark)
    {
        var saved=view.Messages.ToArray(); view.Messages.Clear();
        await TestLongMessageAsync(window,view);
        foreach(var item in saved)view.Messages.Add(item);
        await Settle(window);
        Console.WriteLine("PASS long response in " + (dark?"dark":"light") + " theme");
    }
    private static void Inside(FrameworkElement root,FrameworkElement element,string message)
    {
        var point=element.TransformToAncestor(root).Transform(new Point());
        Check(element.ActualWidth>0 && element.ActualHeight>0 && point.X>=-1 && point.Y>=-1 && point.X+element.ActualWidth<=root.ActualWidth+1 && point.Y+element.ActualHeight<=root.ActualHeight+1,message);
    }
}
