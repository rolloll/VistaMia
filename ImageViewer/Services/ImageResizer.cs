using System.IO;
using ImageMagick;

namespace ImageViewer.Services;

public enum ResizeUnit
{
    Pixel,
    Centimeter,
    Millimeter
}

/// <summary>Batch-resize support for the "웹툰 리사이징" feature: converts a size given in
/// pixels/cm/mm to pixels, then resizes one image to a file via Magick.NET.</summary>
public static class ImageResizer
{
    /// <summary>Converts a size in the given unit to pixels at the given DPI. Pixel unit passes
    /// the value through unchanged; cm/mm use the standard 2.54cm-per-inch / 25.4mm-per-inch
    /// conversion against the DPI.</summary>
    public static double ToPixels(double value, ResizeUnit unit, double dpi) => unit switch
    {
        ResizeUnit.Pixel => value,
        ResizeUnit.Centimeter => value / 2.54 * dpi,
        ResizeUnit.Millimeter => value / 25.4 * dpi,
        _ => value
    };

    /// <summary>Inverse of <see cref="ToPixels"/> - converts a pixel size back to the given unit
    /// at the given DPI, e.g. for redisplaying a value after the unit dropdown changes.</summary>
    public static double FromPixels(double px, ResizeUnit unit, double dpi) => unit switch
    {
        ResizeUnit.Pixel => px,
        ResizeUnit.Centimeter => px / dpi * 2.54,
        ResizeUnit.Millimeter => px / dpi * 25.4,
        _ => px
    };

    /// <summary>
    /// Resizes one image and writes it to <paramref name="destPath"/>, preserving the source's own
    /// format.
    ///
    /// When <paramref name="ignoreAspectRatio"/> is true, the image is stretched to exactly
    /// widthPx x heightPx regardless of its own proportions.
    ///
    /// When false, only ONE dimension - widthPx if <paramref name="primaryIsWidth"/>, otherwise
    /// heightPx - is actually applied; the other is computed from this image's own aspect ratio.
    /// This matters for a batch of files that share one dimension but vary in the other (e.g. a
    /// webtoon episode's pages, all the same width but each a different height): boxing every file
    /// into a single WxH bound taken from just one reference image would shrink whichever pages
    /// are proportionally taller/narrower than that reference below the intended fixed dimension.
    /// Driving off a single dimension keeps every file at exactly that size on its own terms.
    /// </summary>
    public static void ResizeToFile(string sourcePath, string destPath, uint widthPx, uint heightPx, bool ignoreAspectRatio, bool primaryIsWidth = true)
    {
        using var image = new MagickImage(sourcePath);
        var geometry = ignoreAspectRatio
            ? new MagickGeometry(widthPx, heightPx) { IgnoreAspectRatio = true }
            : primaryIsWidth
                ? new MagickGeometry(widthPx, 0)
                : new MagickGeometry(0, heightPx);
        image.Resize(geometry);

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        image.Write(destPath);
    }
}
