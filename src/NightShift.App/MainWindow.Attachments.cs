using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NightShift.Core;

namespace NightShift.App;

public partial class MainWindow
{
    private Func<bool> ClipboardContainsImageProvider = () => Clipboard.ContainsImage();
    private Func<BitmapSource?> ClipboardImageProvider = () => Clipboard.GetImage();
    private void ComposerCanPasteCommand(object sender, CanExecuteRoutedEventArgs e)
    {
        try
        {
            if (!ClipboardContainsImageProvider()) return;
            e.CanExecute = _view.CanAttach; e.Handled = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { }
    }
    private async void ComposerPasteCommand(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (ClipboardImageProvider() is { } bitmap) await ImportBitmapAsync(bitmap);
        }
        catch (Exception ex) { _view.Notice = "Não foi possível colar a imagem: " + ex.Message; }
    }
    private async void AttachClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Anexar imagens ou arquivos", Multiselect = true,
            Filter = "Imagens e arquivos|*.*|Imagens (PNG, JPEG, WebP)|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await ImportAttachmentsAsync(picker.FileNames);
    }
    private async Task ImportAttachmentsAsync(string[] paths)
    {
        try { await _view.AddAttachmentsAsync(paths); }
        catch (Exception ex) { _view.Notice = "Não foi possível anexar: " + ex.Message; }
    }
    private void RemoveAttachmentClick(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: Attachment item }) _view.RemoveAttachment(item); }
    private void AttachmentDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = _view.CanAttach ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private async void AttachmentDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        await ImportAttachmentsAsync(paths);
    }
    private async void AttachmentPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetDataPresent(DataFormats.FileDrop))
        {
            e.CancelCommand();
            if (e.SourceDataObject.GetData(DataFormats.FileDrop) is string[] files) await ImportAttachmentsAsync(files);
            return;
        }
        if (!e.SourceDataObject.GetDataPresent(DataFormats.Bitmap)) return;
        e.CancelCommand();
        if (!_view.CanAttach) return;
        try
        {
            if (e.SourceDataObject.GetData(DataFormats.Bitmap) is not BitmapSource bitmap)
                bitmap = ClipboardImageProvider() ?? throw new InvalidOperationException("Imagem da área de transferência não reconhecida.");
            await ImportBitmapAsync(bitmap);
        }
        catch (Exception ex) { _view.Notice = "Não foi possível colar a imagem: " + ex.Message; }
    }
    private async Task ImportBitmapAsync(BitmapSource bitmap)
    {
        if (!_view.CanAttach) return;
        string? temporary = null;
        try
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 40000000) throw new InvalidOperationException("Cole uma imagem com até 40 megapixels.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var temporaryDirectory = Path.Combine(AppStore.DefaultRoot, "clipboard", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            temporary = Path.Combine(temporaryDirectory, "Imagem colada.png");
            using (var file = File.Create(temporary)) encoder.Save(file);
            await ImportAttachmentsAsync([temporary]);
        }
        finally { if (temporary is not null) { try { File.Delete(temporary); Directory.Delete(Path.GetDirectoryName(temporary)!); } catch (Exception ex) when(ex is IOException or UnauthorizedAccessException) { } } }
    }
}
