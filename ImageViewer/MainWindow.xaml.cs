using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Services;

namespace ImageViewer;

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public int RowNumber { get; set; }

    public string Format => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    public bool IsRawFormat => Format is "PSD" or "CLIP";

    private FileInfo? _fileInfo;
    private FileInfo Info => _fileInfo ??= new FileInfo(FullPath);

    private (int Width, int Height)? _dimensions;
    public (int Width, int Height)? Dimensions
    {
        get => _dimensions;
        set
        {
            _dimensions = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Dimensions)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DimensionsDisplay)));
        }
    }

    public string DimensionsDisplay => Dimensions is { } d ? $"{d.Width} x {d.Height}" : "—";

    public string FileSizeDisplay
    {
        get
        {
            try
            {
                var bytes = Info.Length;
                return bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):N1} MB" : $"{bytes / 1024.0:N0} KB";
            }
            catch { return "—"; }
        }
    }

    public string ModifiedDisplay
    {
        get
        {
            try { return Info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"); }
            catch { return "—"; }
        }
    }

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private const int ThumbnailPixelWidth = 176;
    private const string MaximizeGlyph = "□";
    private const string RestoreGlyph = "▣";

    private readonly ObservableCollection<ThumbnailItem> _thumbnails = new();
    private CancellationTokenSource? _thumbnailLoadCts;
    private int _currentIndex = -1;
    private readonly ImagePanZoomController _panZoom;
    private string? _zoomFolderPath;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialFilePath)
    {
        InitializeComponent();
        ThumbnailList.ItemsSource = _thumbnails;
        FileTable.ItemsSource = _thumbnails;
        _panZoom = new ImagePanZoomController(PreviewScrollViewer, PreviewImage, PreviewScaleTransform);
        PopulateDriveRoots();
        PopulateFormatTags();

        StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();

        if (!string.IsNullOrWhiteSpace(initialFilePath))
        {
            // Opening straight into a single image (e.g. via "Open with") should feel like a
            // lightweight viewer, not a full explorer - start with the folder tree tucked away.
            SetTreeCollapsed(true);
            _ = OpenInitialFileAsync(initialFilePath);
        }
    }

    // Extensions that are really the same format under two spellings - shown once, under the
    // more common name, rather than as two near-duplicate tags side by side.
    private static readonly Dictionary<string, string> FormatDisplayAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPG"] = "JPEG",
        ["TIF"] = "TIFF"
    };

    private void PopulateFormatTags()
    {
        var seen = new HashSet<string>();
        foreach (var ext in ImageLoader.SupportedExtensions)
        {
            var name = ext.TrimStart('.').ToUpperInvariant();
            name = FormatDisplayAliases.GetValueOrDefault(name, name);
            if (!seen.Add(name)) continue;

            var isRaw = name is "PSD" or "CLIP";
            var tag = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource(isRaw ? "Accent2Brush" : "SurfaceBrush"),
                CornerRadius = (CornerRadius)FindResource("RadiusMd"),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(3),
                Opacity = isRaw ? 1.0 : 1.0
            };
            var text = new TextBlock
            {
                Text = name,
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource(isRaw ? "BgBrush" : "TextMutedBrush")
            };
            tag.Child = text;
            FormatTagsPanel.Items.Add(tag);
        }
    }

    // ---------- Title bar ----------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeIcon() => MaximizeButton.Content = WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;

    // ---------- Drag & drop ----------

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;

        var dropped = paths[0];
        if (Directory.Exists(dropped))
            _ = LoadFolderAsync(dropped);
        else if (File.Exists(dropped))
            _ = OpenInitialFileAsync(dropped);
    }

    /// <summary>Handles launch via "Open with" / file association: load the file's folder and select it.</summary>
    private async Task OpenInitialFileAsync(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        await LoadFolderAsync(folder);

        var match = _thumbnails.FirstOrDefault(t => string.Equals(t.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            ThumbnailList.SelectedItem = match;
            ThumbnailList.ScrollIntoView(match);
        }

        SyncTreeToFolder(folder);
    }

    /// <summary>Expands the folder tree down to the given path without changing selection (avoids re-triggering a folder reload).</summary>
    private void SyncTreeToFolder(string folderPath)
    {
        try
        {
            var root = Path.GetPathRoot(folderPath);
            if (string.IsNullOrEmpty(root)) return;

            var current = FolderTree.Items.Cast<TreeViewItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, root, StringComparison.OrdinalIgnoreCase));
            if (current == null) return;

            current.IsExpanded = true; // synchronously triggers lazy population

            var relative = Path.GetRelativePath(root, folderPath);
            var segments = relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            var accumulatedPath = root;
            foreach (var segment in segments)
            {
                accumulatedPath = Path.Combine(accumulatedPath, segment);
                var next = current.Items.Cast<TreeViewItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, accumulatedPath, StringComparison.OrdinalIgnoreCase));
                if (next == null) break;

                current = next;
                current.IsExpanded = true;
            }

            // The containers for just-expanded children aren't measured yet on this tick;
            // defer until after the pending layout pass so BringIntoView scrolls to the right place.
            var target = current;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => target.BringIntoView()));
        }
        catch
        {
            // Best-effort tree sync only; the folder and thumbnails are already loaded regardless.
        }
    }

    // ---------- Folder tree ----------

    private void PopulateDriveRoots()
    {
        FolderTree.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            var item = new TreeViewItem { Header = drive.Name, Tag = drive.RootDirectory.FullName };
            item.Items.Add(new TreeViewItem { Header = "..." }); // placeholder for lazy expansion
            item.Expanded += FolderTreeItem_Expanded;
            FolderTree.Items.Add(item);
        }
    }

    private void FolderTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string path) return;
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem placeholder || placeholder.Tag != null)
            return; // already populated

        item.Items.Clear();
        foreach (var subDirPath in SafeEnumerateDirectories(path))
        {
            var subItem = new TreeViewItem { Header = Path.GetFileName(subDirPath), Tag = subDirPath };
            subItem.Items.Add(new TreeViewItem { Header = "..." });
            subItem.Expanded += FolderTreeItem_Expanded;
            item.Items.Add(subItem);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).OrderBy(p => p).ToList();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: string path })
        {
            PathTextBox.Text = path;
            _ = LoadFolderAsync(path);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) => _ = LoadFolderAsync(PathTextBox.Text);

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "이미지 폴더를 선택하세요",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(PathTextBox.Text) ? PathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PathTextBox.Text = dialog.SelectedPath;
            _ = LoadFolderAsync(dialog.SelectedPath);
        }
    }

    private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = LoadFolderAsync(PathTextBox.Text);
    }

    // ---------- Thumbnail list ----------

    private async Task LoadFolderAsync(string rawFolderPath)
    {
        var folderPath = rawFolderPath.Trim().Trim('"');

        if (!Directory.Exists(folderPath))
        {
            MessageBox.Show(this, $"폴더를 찾을 수 없습니다:\n{folderPath}", "Vista Mia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PathTextBox.Text = folderPath;

        _thumbnailLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbnailLoadCts = cts;

        _thumbnails.Clear();
        ShowEmptyState(true);
        _currentIndex = -1;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folderPath)
                .Where(f => ImageLoader.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"폴더를 여는 중 오류가 발생했습니다:\n{ex.Message}", "Vista Mia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var items = files.Select((f, i) => new ThumbnailItem { FullPath = f, FileName = Path.GetFileName(f), RowNumber = i + 1 }).ToList();
        foreach (var item in items)
            _thumbnails.Add(item);

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ThumbnailHeaderText.Text = $"{items.Count}개 파일";
        ThumbnailFolderText.Text = folderName;
        TitleSubtitleText.Text = folderName;
        BottomStatusText.Text = $"{items.Count}개 파일   |   ←/→ 또는 이전/다음 버튼: 이미지 이동   |   Ctrl+휠: 확대/축소   |   휠: 스크롤   |   드래그: 이동   |   0: 원본 크기 맞춤   |   Ctrl/Shift+클릭: 리사이즈할 파일 여러 개 선택";

        foreach (var item in items)
        {
            if (cts.Token.IsCancellationRequested) return;

            var path = item.FullPath;
            var (bitmap, dimensions) = await Task.Run(
                () => (ImageLoader.Load(path, ThumbnailPixelWidth), ImageDimensionReader.TryGetDimensions(path)),
                cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            item.Thumbnail = bitmap;
            item.Dimensions = dimensions;
        }
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnFileSelected(ThumbnailList.SelectedIndex);

    private void FileTable_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnFileSelected(FileTable.SelectedIndex);

    /// <summary>Grid and table views are two independent controls bound to the same collection;
    /// this keeps whichever one is hidden in sync so switching views doesn't lose the selection.</summary>
    private void OnFileSelected(int index)
    {
        if (index < 0 || index == _currentIndex) return;
        _currentIndex = index;
        if (ThumbnailList.SelectedIndex != index) ThumbnailList.SelectedIndex = index;
        if (FileTable.SelectedIndex != index) FileTable.SelectedIndex = index;
        ShowPreview(_currentIndex);
    }

    private async void ShowPreview(int index)
    {
        if (index < 0 || index >= _thumbnails.Count) return;

        var item = _thumbnails[index];
        StatusText.Text = $"불러오는 중... {item.FileName}";
        ShowEmptyState(false);

        var path = item.FullPath;
        var bitmap = await Task.Run(() => ImageLoader.Load(path));

        if (_currentIndex != index) return; // selection moved on while we were loading

        if (bitmap == null)
        {
            PreviewImage.Source = null;
            StatusText.Text = $"미리보기를 불러올 수 없습니다: {item.FileName}";
            return;
        }

        PreviewImage.Source = bitmap;
        _panZoom.CurrentBitmap = bitmap;

        // Keep the current zoom level when moving between images in the same folder; only reset
        // back to the default fit when the folder itself changed.
        var folder = Path.GetDirectoryName(item.FullPath);
        if (_zoomFolderPath != folder)
        {
            _panZoom.ResetZoom();
            _zoomFolderPath = folder;
        }
        else
        {
            _panZoom.ResetScrollPosition();
        }

        StatusText.Text = $"{item.FileName} ({bitmap.PixelWidth} x {bitmap.PixelHeight})";
        UpdateZoomLabel();
        OpenViewerButton.IsEnabled = true;
        StatusChipPanel.Visibility = Visibility.Visible;
    }

    private void UpdateZoomLabel() => ZoomLabelText.Text = $"{Math.Round(PreviewScaleTransform.ScaleX * 100)}%";

    private void ShowEmptyState(bool show)
    {
        WelcomePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            PreviewImage.Source = null;
            StatusText.Text = string.Empty;
            ZoomLabelText.Text = string.Empty;
            OpenViewerButton.IsEnabled = false;
            StatusChipPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Navigation ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
                SelectRelative(1);
                e.Handled = true;
                break;
            case Key.Left:
                SelectRelative(-1);
                e.Handled = true;
                break;
            case Key.Down:
                _panZoom.ScrollLineDown();
                e.Handled = true;
                break;
            case Key.Up:
                _panZoom.ScrollLineUp();
                e.Handled = true;
                break;
            case Key.Home:
                _panZoom.ScrollToTop();
                e.Handled = true;
                break;
            case Key.End:
                _panZoom.ScrollToBottom();
                e.Handled = true;
                break;
            case Key.PageDown:
                _panZoom.ScrollPageDown();
                e.Handled = true;
                break;
            case Key.PageUp:
                _panZoom.ScrollPageUp();
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                _panZoom.ResetZoom();
                UpdateZoomLabel();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                _panZoom.ZoomBy(1.2);
                UpdateZoomLabel();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                _panZoom.ZoomBy(1 / 1.2);
                UpdateZoomLabel();
                e.Handled = true;
                break;
        }
    }

    private void SelectRelative(int delta)
    {
        if (_thumbnails.Count == 0) return;
        var next = Math.Clamp(_currentIndex + delta, 0, _thumbnails.Count - 1);
        if (next == _currentIndex) return;
        OnFileSelected(next);
        if (ListViewToggle.IsChecked == true)
            FileTable.ScrollIntoView(FileTable.SelectedItem);
        else
            ThumbnailList.ScrollIntoView(ThumbnailList.SelectedItem);
    }

    private void PrevImageButton_Click(object sender, RoutedEventArgs e) => SelectRelative(-1);

    private void NextImageButton_Click(object sender, RoutedEventArgs e) => SelectRelative(1);

    // ---------- Grid / List view toggle ----------

    private void ViewModeToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (FileTable is null || ThumbnailList is null) return; // fires once during XAML init, before both exist

        var showList = ReferenceEquals(sender, ListViewToggle);
        ThumbnailList.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
        FileTable.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Folder tree toggle ----------

    private GridLength _savedTreeWidth = new(230);
    private bool _isTreeCollapsed;

    private void ToggleTreeButton_Click(object sender, RoutedEventArgs e) => SetTreeCollapsed(!_isTreeCollapsed);

    private void SetTreeCollapsed(bool collapsed)
    {
        if (collapsed == _isTreeCollapsed) return;

        if (!collapsed)
        {
            FolderTreeColumn.Width = _savedTreeWidth;
            FolderTreeSplitterColumn.Width = new GridLength(4);
            FolderTreeSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _savedTreeWidth = FolderTreeColumn.Width;
            FolderTreeColumn.Width = new GridLength(0);
            FolderTreeSplitterColumn.Width = new GridLength(0);
            FolderTreeSplitter.Visibility = Visibility.Collapsed;
        }

        ToggleTreeButton.Content = collapsed ? "»" : "«";
        _isTreeCollapsed = collapsed;
    }

    // ---------- Zoom / pan ----------

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _panZoom.OnPreviewMouseWheel(e);
        UpdateZoomLabel();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _panZoom.ZoomBy(1 / 1.2);
        UpdateZoomLabel();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _panZoom.ZoomBy(1.2);
        UpdateZoomLabel();
    }

    private void ZoomFitButton_Click(object sender, RoutedEventArgs e)
    {
        _panZoom.ResetZoom();
        UpdateZoomLabel();
    }

    private void OpenViewerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _thumbnails.Count == 0) return;
        OpenSingleViewer(_currentIndex);
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            PreviewImage_MouseDoubleClick(sender, e);
            return;
        }
        _panZoom.OnImageMouseLeftButtonDown(e);
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _panZoom.OnImageMouseLeftButtonUp(e);

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e) => _panZoom.OnImageMouseMove(e);

    // ---------- Single-image viewer ----------

    private void ThumbnailList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ThumbnailList.SelectedIndex < 0 || _thumbnails.Count == 0) return;
        OpenSingleViewer(ThumbnailList.SelectedIndex);
    }

    private void FileTable_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTable.SelectedIndex < 0 || _thumbnails.Count == 0) return;
        OpenSingleViewer(FileTable.SelectedIndex);
    }

    private void PreviewImage_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_currentIndex < 0 || _thumbnails.Count == 0) return;
        OpenSingleViewer(_currentIndex);
    }

    private void OpenSingleViewer(int index)
    {
        var paths = _thumbnails.Select(t => t.FullPath).ToList();
        var viewer = new SingleViewerWindow(paths, index) { Owner = this };
        viewer.Show();
    }

    // ---------- Batch resize / batch rename ----------

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        // ListView derives from ListBox, so both controls fit through this one reference.
        ListBox activeList = ListViewToggle.IsChecked == true ? FileTable : ThumbnailList;
        activeList.SelectAll();
    }

    private void ResizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_thumbnails.Count == 0)
        {
            MessageBox.Show(this, "먼저 이미지가 있는 폴더를 열어주세요.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Grid and list view are two independent ListBox/ListView controls bound to the same
        // collection (see the class comment on OnFileSelected) - only the one currently visible
        // holds the selection the user actually made.
        // ListView derives from ListBox, so both controls fit through this one reference.
        ListBox activeList = ListViewToggle.IsChecked == true ? FileTable : ThumbnailList;
        var selectedPaths = activeList.SelectedItems.Cast<ThumbnailItem>().Select(t => t.FullPath).ToList();

        if (selectedPaths.Count == 0)
        {
            MessageBox.Show(this, "리사이즈할 파일을 목록에서 선택하세요.\n(Ctrl/Shift+클릭으로 여러 개를 선택할 수 있습니다.)",
                "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new BatchResizeWindow(selectedPaths) { Owner = this };
        window.ShowDialog();
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPath = PathTextBox.Text;
        if (!Directory.Exists(folderPath))
        {
            MessageBox.Show(this, "먼저 폴더를 열어주세요.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new BatchRenameWindow(folderPath) { Owner = this };
        window.RenameApplied += () => _ = LoadFolderAsync(folderPath);
        window.ShowDialog();
    }
}
