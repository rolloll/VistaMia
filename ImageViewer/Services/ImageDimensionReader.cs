using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageViewer.Services;

/// <summary>
/// Reads an image's true pixel dimensions for display purposes (thumbnail captions, the file
/// list, the resize dialog) without decoding full pixel data - a header-only read via
/// MagickImageInfo/BitmapDecoder(DelayCreation), mirroring ImageLoader's per-format dispatch.
/// </summary>
public static class ImageDimensionReader
{
    public static (int Width, int Height)? TryGetDimensions(string path)
    {
        var ext = Path.GetExtension(path);

        try
        {
            if (string.Equals(ext, ".clip", StringComparison.OrdinalIgnoreCase))
                return GetFromClip(path);

            if (ImageLoader.MagickExtensions.Contains(ext))
            {
                var info = new MagickImageInfo(path);
                return ((int)info.Width, (int)info.Height);
            }

            if (ImageLoader.NativeExtensions.Contains(ext))
                return GetFromStream(File.OpenRead(path));

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the image's own embedded DPI (X resolution), for prefilling the resize
    /// dialog's DPI field. Magick.NET can read density from any format it supports (not just the
    /// PSD/WebP/etc. ones ImageLoader routes to it for decoding), so this always goes through
    /// MagickImageInfo regardless of extension. Returns null if the file has no usable density
    /// (e.g. DensityUnit.Undefined, which ImageMagick reports for files with no embedded DPI).</summary>
    public static double? TryGetDpi(string path)
    {
        try
        {
            var info = string.Equals(Path.GetExtension(path), ".clip", StringComparison.OrdinalIgnoreCase)
                ? GetClipMagickInfo(path)
                : new MagickImageInfo(path);

            var density = info?.Density;
            if (density == null || density.X <= 0 || density.Units == DensityUnit.Undefined)
                return null;

            return density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X;
        }
        catch
        {
            return null;
        }
    }

    private static MagickImageInfo? GetClipMagickInfo(string path)
    {
        var png = ClipThumbnailReader.ExtractPreviewPng(path);
        return png == null ? null : new MagickImageInfo(png);
    }

    private static (int, int)? GetFromClip(string path)
    {
        var png = ClipThumbnailReader.ExtractPreviewPng(path);
        return png == null ? null : GetFromStream(new MemoryStream(png));
    }

    private static (int, int) GetFromStream(Stream stream)
    {
        using (stream)
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
    }
}
