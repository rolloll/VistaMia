using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Services;

/// <summary>
/// Shared zoom/pan/fit-to-window mechanics for an Image hosted in a ScrollViewer, driven by a
/// ScaleTransform on the image. Used by both the main explorer window and the single-image viewer
/// so the two stay behaviorally identical (Ctrl+wheel zoom, plain wheel scrolls, drag to pan).
/// </summary>
public sealed class ImagePanZoomController
{
    private readonly ScrollViewer _scrollViewer;
    private readonly UIElement _image;
    private readonly ScaleTransform _scale;

    private bool _isPanning;
    private Point _panStart;
    private double _panStartOffsetX;
    private double _panStartOffsetY;

    public BitmapSource? CurrentBitmap { get; set; }

    public ImagePanZoomController(ScrollViewer scrollViewer, UIElement image, ScaleTransform scale)
    {
        _scrollViewer = scrollViewer;
        _image = image;
        _scale = scale;
    }

    public void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (CurrentBitmap == null) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return; // let the ScrollViewer's default wheel handling scroll the image

        ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    public void ZoomBy(double factor)
    {
        var newScale = Math.Clamp(_scale.ScaleX * factor, 0.05, 20);
        _scale.ScaleX = newScale;
        _scale.ScaleY = newScale;
    }

    /// <summary>Resets to the default view: shrink-to-fit for images larger than the viewport, 100% otherwise.</summary>
    public void ResetZoom()
    {
        var scale = ComputeFitScale();
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;
        _scrollViewer.ScrollToHorizontalOffset(0);
        _scrollViewer.ScrollToVerticalOffset(0);
    }

    private double ComputeFitScale()
    {
        if (CurrentBitmap == null) return 1;

        var viewportWidth = _scrollViewer.ViewportWidth > 0 ? _scrollViewer.ViewportWidth : _scrollViewer.ActualWidth;
        var viewportHeight = _scrollViewer.ViewportHeight > 0 ? _scrollViewer.ViewportHeight : _scrollViewer.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0) return 1;

        var fitScale = Math.Min(viewportWidth / CurrentBitmap.PixelWidth, viewportHeight / CurrentBitmap.PixelHeight);
        return Math.Min(1, fitScale); // shrink to fit, but never upscale by default
    }

    public void OnImageMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (CurrentBitmap == null) return;
        _isPanning = true;
        _panStart = e.GetPosition(_scrollViewer);
        _panStartOffsetX = _scrollViewer.HorizontalOffset;
        _panStartOffsetY = _scrollViewer.VerticalOffset;
        _image.CaptureMouse();
    }

    public void OnImageMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _isPanning = false;
        _image.ReleaseMouseCapture();
    }

    public void OnImageMouseMove(MouseEventArgs e)
    {
        if (!_isPanning) return;
        var current = e.GetPosition(_scrollViewer);
        var deltaX = current.X - _panStart.X;
        var deltaY = current.Y - _panStart.Y;
        _scrollViewer.ScrollToHorizontalOffset(_panStartOffsetX - deltaX);
        _scrollViewer.ScrollToVerticalOffset(_panStartOffsetY - deltaY);
    }
}
