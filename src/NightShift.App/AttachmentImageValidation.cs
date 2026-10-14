using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NightShift.App;

internal static class AttachmentImageValidation
{
    public static Task ValidateAsync(string path) => Task.Run(() =>
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidOperationException("A imagem está vazia.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 40_000_000)
                throw new InvalidOperationException("A imagem deve ter até 40 megapixels.");
            var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
            if (stride <= 0) throw new InvalidOperationException("Formato de imagem não reconhecido.");
            var rows = Math.Max(1, Math.Min(256, 8_388_608 / stride));
            var buffer = new byte[checked(stride * rows)];
            for (var y = 0; y < frame.PixelHeight; y += rows)
                frame.CopyPixels(new Int32Rect(0, y, frame.PixelWidth, Math.Min(rows, frame.PixelHeight - y)), buffer, stride, 0);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException or System.IO.FileFormatException)
        {
            throw new InvalidOperationException("Não foi possível ler a imagem completa. Use PNG ou JPEG válido.", ex);
        }
    });
}
