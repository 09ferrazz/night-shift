using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using NightShift.Core;

namespace NightShift.App;

public sealed class AttachmentThumbnail : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Attachment { IsImage: true } item) return null;
        try
        {
            using var stream = System.IO.File.OpenRead(item.Path);
            var image = new BitmapImage(); image.BeginInit(); image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 96;
            image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or System.IO.FileFormatException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
