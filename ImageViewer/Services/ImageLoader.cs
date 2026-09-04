using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageViewer.Services;

/// <summary>
/// Loads a displayable BitmapSource for any supported file, dispatching by extension:
/// common raster formats decode natively via WPF, PSD/WebP/etc. decode via Magick.NET
/// (which flattens PSD layers into a single composite), and .clip files go through
/// <see cref="ClipThumbnailReader"/> to pull out the embedded preview PNG.
/// </summary>
public static class ImageLoader
{
    // internal rather than private so ImageDimensionReader can dispatch by extension the same way
    // this class does, without duplicating (and risking drift from) the list.
    internal static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    internal static readonly HashSet<string> MagickExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".psb", ".webp", ".ico", ".heic", ".heif"
    };

    public static readonly IReadOnlyList<string> SupportedExtensions =
        NativeExtensions.Concat(MagickExtensions).Append(".clip").ToArray();

    /// <param name="decodePixelWidth">Pass a small value (e.g. 160) for thumbnails to keep decoding cheap; null for full-resolution preview.</param>
    public static BitmapSource? Load(string path, int? decodePixelWidth = null)
    {
        var ext = Path.GetExtension(path);

        try
        {
            if (string.Equals(ext, ".clip", StringComparison.OrdinalIgnoreCase))
                return LoadClip(path, decodePixelWidth);

            if (MagickExtensions.Contains(ext))
                return LoadWithMagick(path, decodePixelWidth);

            if (NativeExtensions.Contains(ext))
                return LoadNative(path, decodePixelWidth);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadClip(string path, int? decodePixelWidth)
    {
        var png = ClipThumbnailReader.ExtractPreviewPng(path);
        return png == null ? null : LoadFromBytes(png, decodePixelWidth);
    }

    private static BitmapSource LoadWithMagick(string path, int? decodePixelWidth)
    {
        using var image = new MagickImage(path);
        if (decodePixelWidth.HasValue && image.Width > decodePixelWidth.Value)
            image.Resize((uint)decodePixelWidth.Value, 0);

        image.Format = MagickFormat.Png;
        using var ms = new MemoryStream();
        image.Write(ms);
        ms.Position = 0;
        return LoadFromStream(ms, null);
    }

    private static BitmapImage LoadNative(string path, int? decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth.HasValue)
            bitmap.DecodePixelWidth = decodePixelWidth.Value;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapImage LoadFromBytes(byte[] bytes, int? decodePixelWidth)
    {
        using var ms = new MemoryStream(bytes);
        return LoadFromStream(ms, decodePixelWidth);
    }

    private static BitmapImage LoadFromStream(Stream stream, int? decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth.HasValue)
            bitmap.DecodePixelWidth = decodePixelWidth.Value;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
