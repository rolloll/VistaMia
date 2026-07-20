using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Services;

namespace ImageViewer;

/// <summary>Single-image viewer window in the style of classic image viewers: borderless chrome,
/// a flat icon+text menu row (파일/EXIF/HDR/이미지/슬라이드 쇼/사진 보관함), and a floating settings button.</summary>
public partial class SingleViewerWindow : Window
{
    private const string MaximizeGlyph = "□";
    private const string RestoreGlyph = "▣";
    private const string PlayGlyph = "▶";
    private const string PauseGlyph = "⏸";

    private readonly List<string> _filePaths;
    private readonly ImagePanZoomController _panZoom;
    private readonly DispatcherTimer _slideshowTimer = new();

    private int _currentIndex;
    private BitmapSource? _originalBitmap;
    private bool _hdrEnabled;
    private bool _isSlideshowRunning;

    public SingleViewerWindow(List<string> filePaths, int startIndex)
    {
        InitializeComponent();
        _filePaths = filePaths;
        _panZoom = new ImagePanZoomController(PreviewScrollViewer, PreviewImage, PreviewScaleTransform);

        _slideshowTimer.Tick += (_, _) => SelectRelative(1, wrap: true);

        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();

        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, filePaths.Count - 1));
        ShowImage(_currentIndex);
    }

    // ---------- Image loading & navigation ----------

    private async void ShowImage(int index)
    {
        if (index < 0 || index >= _filePaths.Count) return;

        CloseAllFlyouts();
        PreviewRotateTransform.Angle = 0;

        var path = _filePaths[index];
        TitleText.Text = Path.GetFileName(path);
        StatusText.Text = "불러오는 중...";
        EmptyStateText.Visibility = Visibility.Collapsed;

        var bitmap = await Task.Run(() => ImageLoader.Load(path));
        if (_currentIndex != index) return;

        _originalBitmap = bitmap;
        if (bitmap == null)
        {
            PreviewImage.Source = null;
            EmptyStateText.Visibility = Visibility.Visible;
            StatusText.Text = $"불러올 수 없습니다: {Path.GetFileName(path)}";
            return;
        }

        ApplyHdrState();
        _panZoom.CurrentBitmap = bitmap;
        _panZoom.ResetZoom();
        StatusText.Text = $"{Path.GetFileName(path)}   ({bitmap.PixelWidth} x {bitmap.PixelHeight})   [{index + 1}/{_filePaths.Count}]";
    }

    private void SelectRelative(int delta, bool wrap = false)
    {
        if (_filePaths.Count == 0) return;

        var next = _currentIndex + delta;
        if (wrap)
            next = ((next % _filePaths.Count) + _filePaths.Count) % _filePaths.Count;
        else
            next = Math.Clamp(next, 0, _filePaths.Count - 1);

        if (next == _currentIndex) return;
        _currentIndex = next;
        ShowImage(_currentIndex);
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => SelectRelative(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => SelectRelative(1);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Right:
                SelectRelative(1);
                e.Handled = true;
                break;
            case Key.Left:
                SelectRelative(-1);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                _panZoom.ResetZoom();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                _panZoom.ZoomBy(1.2);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                _panZoom.ZoomBy(1 / 1.2);
                e.Handled = true;
                break;
        }
    }

    // ---------- Zoom / pan (delegates to the shared controller) ----------

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => _panZoom.OnPreviewMouseWheel(e);

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _panZoom.OnImageMouseLeftButtonDown(e);

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _panZoom.OnImageMouseLeftButtonUp(e);

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e) => _panZoom.OnImageMouseMove(e);

    // ---------- Title bar ----------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeIcon() => MaximizeButton.Content = WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;

    // ---------- Flat menu row ----------

    private void CloseAllFlyouts()
    {
        ExifPanel.Visibility = Visibility.Collapsed;
        ImagePanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        FileMenuButton.IsChecked = false;
        ExifMenuButton.IsChecked = false;
        ImageMenuButton.IsChecked = false;
    }

    private void FileMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = FileMenuButton.IsChecked == true;
        CloseAllFlyouts();
        if (!opening) return;

        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "이미지 파일|*" + string.Join(";*", ImageLoader.SupportedExtensions),
            Title = "이미지 열기"
        };
        FileMenuButton.IsChecked = false;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var folder = Path.GetDirectoryName(dialog.FileName);
            if (folder == null) return;

            _filePaths.Clear();
            _filePaths.AddRange(Directory.EnumerateFiles(folder)
                .Where(f => ImageLoader.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            _currentIndex = Math.Max(0, _filePaths.FindIndex(f => string.Equals(f, dialog.FileName, StringComparison.OrdinalIgnoreCase)));
            ShowImage(_currentIndex);
        }
    }

    private void ExifMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = ExifMenuButton.IsChecked == true;
        CloseAllFlyouts();
        ExifMenuButton.IsChecked = opening;
        ExifPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening) ExifText.Text = BuildExifText(_filePaths.ElementAtOrDefault(_currentIndex));
    }

    private static string BuildExifText(string? path)
    {
        if (path == null || !File.Exists(path)) return "정보를 표시할 수 없습니다.";

        var info = new FileInfo(path);
        var lines = new List<string>
        {
            $"이름: {info.Name}",
            $"경로: {info.DirectoryName}",
            $"크기: {info.Length / 1024.0:N0} KB",
            $"수정한 날짜: {info.LastWriteTime:yyyy-MM-dd HH:mm}"
        };

        try
        {
            if (BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                    .Frames[0].Metadata is BitmapMetadata meta)
            {
                if (meta.CameraManufacturer is { Length: > 0 } make) lines.Add($"카메라 제조사: {make}");
                if (meta.CameraModel is { Length: > 0 } model) lines.Add($"카메라 모델: {model}");
                if (meta.DateTaken is { Length: > 0 } taken) lines.Add($"촬영 날짜: {taken}");
            }
        }
        catch
        {
            // Not all formats expose EXIF metadata (PSD/CLIP previews, PNG, etc.) - that's expected.
        }

        return string.Join("\n", lines);
    }

    private void HdrToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _hdrEnabled = HdrToggleButton.IsChecked == true;
        ApplyHdrState();
    }

    /// <summary>
    /// "HDR" here means a simple, real brightness/contrast boost (linear pixel gain) applied on demand -
    /// not true HDR tone-mapping, which is out of scope for a lightweight viewer.
    /// </summary>
    private void ApplyHdrState()
    {
        if (_originalBitmap == null) return;
        PreviewImage.Source = _hdrEnabled ? BrightnessBoost(_originalBitmap, 1.25) : _originalBitmap;
    }

    private static BitmapSource BrightnessBoost(BitmapSource source, double factor)
    {
        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)Math.Min(255, pixels[i] * factor);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * factor);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * factor);
        }

        var result = new WriteableBitmap(width, height, source.DpiX, source.DpiY, System.Windows.Media.PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        result.Freeze();
        return result;
    }

    private void ImageMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = ImageMenuButton.IsChecked == true;
        CloseAllFlyouts();
        ImageMenuButton.IsChecked = opening;
        ImagePanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) =>
        PreviewRotateTransform.Angle = (PreviewRotateTransform.Angle - 90) % 360;

    private void RotateRightButton_Click(object sender, RoutedEventArgs e) =>
        PreviewRotateTransform.Angle = (PreviewRotateTransform.Angle + 90) % 360;

    private void FitButton_Click(object sender, RoutedEventArgs e) => _panZoom.ResetZoom();

    private void SlideshowButton_Click(object sender, RoutedEventArgs e)
    {
        _isSlideshowRunning = !_isSlideshowRunning;
        if (_isSlideshowRunning)
        {
            _slideshowTimer.Interval = TimeSpan.FromSeconds(SlideshowIntervalSlider.Value);
            _slideshowTimer.Start();
            SlideshowIcon.Text = PauseGlyph;
            SlideshowLabel.Text = "정지";
        }
        else
        {
            _slideshowTimer.Stop();
            SlideshowIcon.Text = PlayGlyph;
            SlideshowLabel.Text = "슬라이드 쇼";
        }
        SlideshowButton.IsChecked = _isSlideshowRunning;
    }

    private void LibraryButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var opening = SettingsPanel.Visibility != Visibility.Visible;
        CloseAllFlyouts();
        SettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SlideshowIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SlideshowIntervalText == null) return;
        SlideshowIntervalText.Text = $"{(int)e.NewValue}초";
        if (_isSlideshowRunning)
            _slideshowTimer.Interval = TimeSpan.FromSeconds(e.NewValue);
    }

    protected override void OnClosed(EventArgs e)
    {
        _slideshowTimer.Stop();
        base.OnClosed(e);
    }
}
