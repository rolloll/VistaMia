using System.IO;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Services;

namespace ImageViewer;

/// <summary>Batch-resizes a fixed list of source images (the currently loaded folder's images,
/// passed in by MainWindow) into a chosen destination folder, at a width/height given in
/// pixels/cm/mm. Source files are only ever read - results are written under a separate
/// destination path, never back over the originals.</summary>
public partial class BatchResizeWindow : Window
{
    private readonly List<string> _sourcePaths;

    // The first source image's own pixel size - used both to prefill the fields with its real
    // size on open, and as the aspect-ratio reference while "비율 무시" is unchecked.
    private double? _sourceWidthPx;
    private double? _sourceHeightPx;

    // Guards against WidthTextBox/HeightTextBox's mutual aspect-ratio sync re-triggering itself
    // when one box's TextChanged handler writes into the other.
    private bool _isSyncingSize;

    private ResizeUnit _previousUnit = ResizeUnit.Pixel;

    // While "비율 무시" is unchecked, only ONE dimension is actually applied per file (the other
    // floats per that file's own aspect ratio) - see ImageResizer.ResizeToFile. True = width is
    // the one being applied. Starts from whichever dimension is shared across every selected file
    // (e.g. a webtoon episode's same-width, different-height pages), and flips to whichever field
    // the user actually types into from then on.
    private bool _primaryIsWidth = true;

    public BatchResizeWindow(List<string> sourcePaths)
    {
        InitializeComponent();
        _sourcePaths = sourcePaths;
        SourceCountText.Text = $"{_sourcePaths.Count}개 파일 선택됨";

        if (_sourcePaths.Count > 0 && ImageDimensionReader.TryGetDimensions(_sourcePaths[0]) is { } dims)
        {
            _sourceWidthPx = dims.Width;
            _sourceHeightPx = dims.Height;

            _isSyncingSize = true;
            WidthTextBox.Text = dims.Width.ToString();
            HeightTextBox.Text = dims.Height.ToString();
            _isSyncingSize = false;
        }

        DetectPrimaryDimension();
        UpdatePrimaryHintText();

        if (_sourcePaths.Count > 0 && ImageDimensionReader.TryGetDpi(_sourcePaths[0]) is { } dpi)
            DpiTextBox.Text = Math.Round(dpi).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Looks at every selected file's own pixel size (not just the reference image used
    /// for the field prefill) to find which dimension - if any - is actually shared across the
    /// whole batch, and defaults to driving off that one.</summary>
    private void DetectPrimaryDimension()
    {
        if (_sourcePaths.Count < 2) return;

        var dims = _sourcePaths.Select(ImageDimensionReader.TryGetDimensions).Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (dims.Count < 2) return;

        var allWidthsEqual = dims.All(d => d.Width == dims[0].Width);
        var allHeightsEqual = dims.All(d => d.Height == dims[0].Height);

        // Widths uniform (heights vary, or both happen to be uniform) -> drive off width.
        // Only heights uniform -> drive off height. Neither uniform -> leave the default (width).
        if (!allWidthsEqual && allHeightsEqual)
            _primaryIsWidth = false;
    }

    private void UpdatePrimaryHintText()
    {
        if (PrimaryHintText == null) return;
        PrimaryHintText.Text = IgnoreAspectCheckBox?.IsChecked == true
            ? "모든 파일이 정확히 이 가로x세로 크기로 늘어나거나 줄어듭니다 (원본 비율 무시)."
            : _primaryIsWidth
                ? "가로를 기준으로 맞추고, 세로는 파일마다 원본 비율대로 자동 계산됩니다."
                : "세로를 기준으로 맞추고, 가로는 파일마다 원본 비율대로 자동 계산됩니다.";
    }

    private void IgnoreAspectCheckBox_Changed(object sender, RoutedEventArgs e) => UpdatePrimaryHintText();

    private ResizeUnit SelectedUnit => (ResizeUnit)UnitCombo.SelectedIndex;

    private void UnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DpiTextBox == null) return; // fires once during XAML init, before the field exists
        DpiTextBox.IsEnabled = SelectedUnit != ResizeUnit.Pixel;

        // Re-express whatever is currently typed in the old unit as the same physical size in the
        // newly selected unit, instead of leaving e.g. a pixel count sitting in the cm field.
        if (!double.TryParse(DpiTextBox.Text, out var dpi) || dpi <= 0) dpi = 96;

        _isSyncingSize = true;
        if (double.TryParse(WidthTextBox.Text, out var width))
            WidthTextBox.Text = FormatSize(ImageResizer.FromPixels(ImageResizer.ToPixels(width, _previousUnit, dpi), SelectedUnit, dpi));
        if (double.TryParse(HeightTextBox.Text, out var height))
            HeightTextBox.Text = FormatSize(ImageResizer.FromPixels(ImageResizer.ToPixels(height, _previousUnit, dpi), SelectedUnit, dpi));
        _isSyncingSize = false;

        _previousUnit = SelectedUnit;
    }

    /// <summary>While "비율 무시" is unchecked, keeps the two size fields locked to the reference
    /// image's aspect ratio - editing one immediately recalculates the other.</summary>
    private void WidthTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingSize) return;

        _primaryIsWidth = true;
        UpdatePrimaryHintText();

        if (IgnoreAspectCheckBox?.IsChecked == true) return;
        if (_sourceWidthPx is not > 0 || _sourceHeightPx is not > 0) return;
        if (!double.TryParse(WidthTextBox.Text, out var width) || width <= 0) return;

        var newHeight = width * (_sourceHeightPx.Value / _sourceWidthPx.Value);
        _isSyncingSize = true;
        HeightTextBox.Text = FormatSize(newHeight);
        _isSyncingSize = false;
    }

    private void HeightTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingSize) return;

        _primaryIsWidth = false;
        UpdatePrimaryHintText();

        if (IgnoreAspectCheckBox?.IsChecked == true) return;
        if (_sourceWidthPx is not > 0 || _sourceHeightPx is not > 0) return;
        if (!double.TryParse(HeightTextBox.Text, out var height) || height <= 0) return;

        var newWidth = height * (_sourceWidthPx.Value / _sourceHeightPx.Value);
        _isSyncingSize = true;
        WidthTextBox.Text = FormatSize(newWidth);
        _isSyncingSize = false;
    }

    // Pixel sizes only make sense as whole numbers; cm/mm keep a couple of decimals.
    private string FormatSize(double value) => SelectedUnit == ResizeUnit.Pixel
        ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void BrowseDestButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "저장할 폴더를 선택하세요",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(DestPathTextBox.Text) ? DestPathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DestPathTextBox.Text = dialog.SelectedPath;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(WidthTextBox.Text, out var widthValue) || widthValue <= 0)
        {
            MessageBox.Show(this, "가로 값을 올바르게 입력하세요.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(HeightTextBox.Text, out var heightValue) || heightValue <= 0)
        {
            MessageBox.Show(this, "세로 값을 올바르게 입력하세요.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(DestPathTextBox.Text))
        {
            MessageBox.Show(this, "저장 폴더를 선택하세요.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(DpiTextBox.Text, out var dpi) || dpi <= 0) dpi = 96;

        var destDir = DestPathTextBox.Text;
        var unit = SelectedUnit;
        var ignoreAspect = IgnoreAspectCheckBox.IsChecked == true;
        var primaryIsWidth = _primaryIsWidth;
        var widthPx = (uint)Math.Max(1, Math.Round(ImageResizer.ToPixels(widthValue, unit, dpi)));
        var heightPx = (uint)Math.Max(1, Math.Round(ImageResizer.ToPixels(heightValue, unit, dpi)));

        RunButton.IsEnabled = false;
        Progress.Maximum = _sourcePaths.Count;
        Progress.Value = 0;
        var failed = new List<string>();

        await Task.Run(() =>
        {
            foreach (var src in _sourcePaths)
            {
                try
                {
                    var destPath = Path.Combine(destDir, Path.GetFileName(src));
                    ImageResizer.ResizeToFile(src, destPath, widthPx, heightPx, ignoreAspect, primaryIsWidth);
                }
                catch
                {
                    failed.Add(Path.GetFileName(src));
                }
                Dispatcher.Invoke(() => Progress.Value++);
            }
        });

        RunButton.IsEnabled = true;

        var message = failed.Count == 0
            ? $"{_sourcePaths.Count}개 파일을 리사이즈했습니다.\n저장 위치: {destDir}"
            : $"{_sourcePaths.Count - failed.Count}개 성공, {failed.Count}개 실패:\n{string.Join(", ", failed)}";
        MessageBox.Show(this, message, "리사이즈 완료", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
