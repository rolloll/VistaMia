using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace ImageViewer.Services;

/// <summary>
/// Previews Adobe Illustrator (.ai) files. Illustrator saves a PDF-compatible stream inside the
/// file by default, so the first page (the artboard) is rendered through Windows' built-in PDF
/// renderer (Windows.Data.Pdf) - no Ghostscript or extra native library needed. Very old or
/// "PDF compatibility: off" .ai files have no PDF stream; those return null (no preview).
/// </summary>
internal static class AiPreviewReader
{
    // PdfPage.Size is in 1/96-inch units; Illustrator treats 1 artboard point as 1 px (72 per inch).
    private const double DipToPoint = 72.0 / 96.0;

    // Full-resolution previews are rendered at least this large on the long side (so zooming in
    // stays reasonably sharp) but capped so a huge artboard can't blow up memory.
    private const double MinLongSidePx = 2000;
    private const double MaxLongSidePx = 4096;

    /// <summary>Artboard size in Illustrator pixels (1 pt = 1 px), or null if unreadable.</summary>
    public static (int Width, int Height)? TryGetSize(string path)
    {
        return Run(async () =>
        {
            using var fs = OpenPdfStream(path);
            if (fs == null) return ((int, int)?)null;
            var doc = await PdfDocument.LoadFromStreamAsync(fs.AsRandomAccessStream());
            using var page = doc.GetPage(0);
            return ((int)Math.Round(page.Size.Width * DipToPoint), (int)Math.Round(page.Size.Height * DipToPoint));
        });
    }

    /// <param name="decodePixelWidth">Target width for a thumbnail; null renders a full-size preview.</param>
    /// <returns>PNG bytes of page 1 on a white background, or null if the file has no PDF stream.</returns>
    public static byte[]? RenderPreviewPng(string path, int? decodePixelWidth)
    {
        return Run(async () =>
        {
            using var fs = OpenPdfStream(path);
            if (fs == null) return (byte[]?)null;
            var doc = await PdfDocument.LoadFromStreamAsync(fs.AsRandomAccessStream());
            using var page = doc.GetPage(0);

            double w = page.Size.Width, h = page.Size.Height;
            double scale;
            if (decodePixelWidth.HasValue)
            {
                scale = decodePixelWidth.Value / w;
            }
            else
            {
                var longSide = Math.Max(w, h);
                scale = Math.Clamp(MinLongSidePx, longSide, MaxLongSidePx) / longSide;
            }

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(w * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(h * scale)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
            };

            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, options);

            var bytes = new byte[output.Size];
            await output.AsStreamForRead().ReadExactlyAsync(bytes);
            return bytes;
        });
    }

    /// <summary>Opens the file only if it actually contains a PDF stream (a "%PDF-" header near the
    /// start); returns null for PostScript-only .ai files, which Windows can't render.</summary>
    private static FileStream? OpenPdfStream(string path)
    {
        var fs = File.OpenRead(path);
        var head = new byte[1024];
        var read = fs.Read(head, 0, head.Length);
        fs.Position = 0;
        if (System.Text.Encoding.ASCII.GetString(head, 0, read).Contains("%PDF-", StringComparison.Ordinal))
            return fs;

        fs.Dispose();
        return null;
    }

    // The WinRT calls are async; run them on the thread pool and block, so the (synchronous,
    // already-off-UI-thread) ImageLoader callers don't risk a sync-context deadlock.
    private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
}
