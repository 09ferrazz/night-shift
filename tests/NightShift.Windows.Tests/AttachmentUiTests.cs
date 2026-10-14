using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NightShift.App;
using NightShift.Core;

internal static partial class Program
{
    private static async Task TestAttachmentsAndFeedbackAsync(MainWindow window, MainViewModel view, string output)
    {
        var pixel = BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[] {255,50,100,255, 0,80,240,255, 255,50,100,255, 0,80,240,255},8);
        var png = Path.Combine(output,"sample.png"); var file = Path.Combine(output,"specification.txt");
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixel)); using(var stream=File.Create(png))encoder.Save(stream);
        await File.WriteAllTextAsync(file,"Attachment UI fixture; no user file.");
        foreach(var invalid in new[] {(Name:"truncated.png",Bytes:new byte[]{137,80,78,71,13,10,26,10}),(Name:"truncated.jpg",Bytes:new byte[]{255,216,255})})
        {
            var invalidPath=Path.Combine(output,invalid.Name);await File.WriteAllBytesAsync(invalidPath,invalid.Bytes);
            bool rejected=false;try {await view.AddAttachmentsAsync([invalidPath]);}catch(InvalidOperationException){rejected=true;}
            Check(rejected && !view.HasAttachments,"Truncated image was accepted after its valid signature");
        }
        var composer=(TextBox)window.FindName("Composer");
        var surface=(Border)window.FindName("ComposerSurface");
        var attach=(Button)window.FindName("AttachButton");
        Check(attach is not null,"Attach action missing");
        view.Draft="Confira estas referências";
        var clipboardImage=typeof(MainWindow).GetField("ClipboardImageProvider",BindingFlags.Instance|BindingFlags.NonPublic);
        var clipboardHasImage=typeof(MainWindow).GetField("ClipboardContainsImageProvider",BindingFlags.Instance|BindingFlags.NonPublic);
        Check(clipboardImage is not null && clipboardHasImage is not null,"Composer has no clipboard-image command route for Ctrl+V");
        var originalProvider=clipboardImage.GetValue(window);
        var originalContains=clipboardHasImage.GetValue(window);
        clipboardImage.SetValue(window,(Func<BitmapSource?>)(()=>pixel));
        clipboardHasImage.SetValue(window,(Func<bool>)(()=>true));
        try
        {
            ApplicationCommands.Paste.Execute(null,composer);
            for(int i=0;i<100 && !view.HasAttachments;i++)await Task.Delay(20);
            Check(view.Attachments.Count==1 && view.Attachments[0].IsImage,"Ctrl+V command did not attach the image");
            view.RemoveAttachment(view.Attachments[0]);
        }
        finally { clipboardImage.SetValue(window,originalProvider); clipboardHasImage.SetValue(window,originalContains); }
        var textPaste=new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText,"texto comum"),false,DataFormats.UnicodeText) {RoutedEvent=DataObject.PastingEvent};
        composer.RaiseEvent(textPaste);
        Check(!textPaste.CommandCancelled,"Plain text paste was intercepted as an attachment");
        var paste=new DataObjectPastingEventArgs(new DataObject(DataFormats.Bitmap,pixel),false,DataFormats.Bitmap) {RoutedEvent=DataObject.PastingEvent};
        composer.RaiseEvent(paste);
        for(int i=0;i<100 && !view.HasAttachments;i++)await Task.Delay(20);
        Check(paste.CommandCancelled && view.Attachments.Count==1 && view.Attachments[0].IsImage,"Pasted image was not attached");
        var drop=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.NonPublic,null,[new DataObject(DataFormats.FileDrop,new[] {file}),DragDropKeyStates.None,DragDropEffects.Copy,surface,new Point(5,5)],null)!;
        drop.RoutedEvent=DragDrop.PreviewDropEvent;
        surface.RaiseEvent(drop);
        for(int i=0;i<100 && view.Attachments.Count<2;i++)await Task.Delay(20);
        Check(view.Attachments.Count==2 && view.Draft=="Confira estas referências","Drop lost attachment or draft");
        var root=(FrameworkElement)window.Content;
        var session=(CodexSession)typeof(MainViewModel).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        foreach(var dark in new[] {false,true})
        {
            view.IsDarkTheme=dark; view.Notice="";
            await Settle(window);
            var sidebar=(Border)window.FindName("NavigationPanel");
            var sidebarBg=((SolidColorBrush)sidebar.Background).Color;
            var sidebarSearch=(TextBox)window.FindName("SidebarSearch");
            var sidebarInk=((SolidColorBrush)sidebarSearch.Foreground).Color;
            Check(dark ? sidebarBg.R<80 && sidebarInk.R>200 : sidebarBg.R>200 && sidebarInk.R<100,
                "Sidebar did not follow the selected theme with legible text");
            foreach(var size in new[] {(1280,820),(760,560)})
            {
                window.Width=size.Item1;window.Height=size.Item2;await Settle(window);
                Inside(root,(FrameworkElement)window.FindName("NightToggle"),"Attachments clipped the night toggle");
                Inside(root,(FrameworkElement)window.FindName("PrimaryAction"),"Attachments clipped the send/stop action");
                var strip=(ScrollViewer)window.FindName("AttachmentStrip");
                Check(strip.IsVisible && Children<Image>(strip).Any(i=>i.Source is BitmapSource),"Native thumbnail not rendered");
                Capture(window,Path.Combine(output,$"attachments-{(dark?"dark":"light")}-{size.Item1}.png"));
            }
            var hoverKey=(DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            var pressedKey=(DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            foreach(var button in new[] {attach!,(Button)window.FindName("ActionsButton"),(Button)window.FindName("SessionNav")})
            {
                button.ApplyTemplate(); var box=(Border)button.Template.FindName("Box",button);
                var normal=((SolidColorBrush)box.Background).Color;
                var position=button.TransformToAncestor(root).Transform(new Point());
                button.SetValue(hoverKey,true);await Settle(window);
                var hover=((SolidColorBrush)box.Background).Color;
                Check(hover!=normal && button.TransformToAncestor(root).Transform(new Point())==position,"Hover lacks visible feedback or moved the button");
                button.SetValue(pressedKey,true);await Settle(window);
                Check(((SolidColorBrush)box.Background).Color!=hover,"Pressed feedback is indistinguishable");
                button.SetValue(pressedKey,false);button.SetValue(hoverKey,false);
            }
            typeof(MainWindow).GetMethod("ShowPage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,["Activity"]);await Settle(window);
            var five=(ProgressBar)window.FindName("ActivityFiveHourBar");var weekly=(ProgressBar)window.FindName("ActivityWeeklyBar");
            Check(five.Value==view.FiveHourUsed && weekly.Value==view.WeeklyUsed && five.IsVisible && weekly.IsVisible,"Activity quota is inconsistent with the popup");
            Inside(root,weekly,"Weekly quota clipped in Activity");
            Capture(window,Path.Combine(output,$"activity-limits-{(dark?"dark":"light")}.png"));
            typeof(MainWindow).GetMethod("ShowPage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,["Session"]);await Settle(window);
        }
        session.Shift.Arm(DateTimeOffset.UtcNow,1);session.Shift.WaitForQuota(DateTimeOffset.UtcNow.AddMinutes(20));
        var pending=(List<PendingRequest>)typeof(CodexSession).GetField("_pending",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
        pending.Add(new(JsonSerializer.SerializeToElement(88),"item/tool/requestUserInput",JsonSerializer.SerializeToElement(new { })));
        view.Notice=new string('x',3000);await Settle(window);
        Inside(root,(FrameworkElement)window.FindName("NightToggle"),"Attachments plus notices clipped controls");
        pending.Clear();session.Shift.Stop();view.Notice="";
        var remove=Children<Button>((ScrollViewer)window.FindName("AttachmentStrip")).First(b=>b.Tag is Attachment);
        var removedCopy=((Attachment)remove.Tag).Path;
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle(window);
        Check(view.Attachments.Count==1 && File.Exists(file) && !File.Exists(removedCopy),"Remove failed, retained a temporary copy or deleted the original file");
        await view.NewSessionAsync();await Settle(window);
        Check(!view.HasAttachments && !((ScrollViewer)window.FindName("AttachmentStrip")).IsVisible,"New conversation retained attachments");
        Console.WriteLine("PASS native paste/drop/remove thumbnails, Activity quotas, hover/pressed states and compact attachment layout (simulated input)");
    }
}
