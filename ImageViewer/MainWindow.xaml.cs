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
        _panZoom = new ImagePanZoomController(PreviewScrollViewer, PreviewImage, PreviewScaleTransform);
        PopulateDriveRoots();

        if (!string.IsNullOrWhiteSpace(initialFilePath))
        {
            // Opening straight into a single image (e.g. via "Open with") should feel like a
            // lightweight viewer, not a full explorer - start with the folder tree tucked away.
            SetTreeCollapsed(true);
            _ = OpenInitialFileAsync(initialFilePath);
        }
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

        var items = files.Select(f => new ThumbnailItem { FullPath = f, FileName = Path.GetFileName(f) }).ToList();
        foreach (var item in items)
            _thumbnails.Add(item);

        BottomStatusText.Text = $"{items.Count}개 파일   |   ←/→ 또는 이전/다음 버튼: 이미지 이동   |   Ctrl+휠: 확대/축소   |   휠: 스크롤   |   드래그: 이동   |   0: 원본 크기 맞춤";

        foreach (var item in items)
        {
            if (cts.Token.IsCancellationRequested) return;

            var path = item.FullPath;
            var bitmap = await Task.Run(() => ImageLoader.Load(path, ThumbnailPixelWidth), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            item.Thumbnail = bitmap;
        }
    }

    private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = ThumbnailList.SelectedIndex;
        if (index < 0) return;
        _currentIndex = index;
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

        StatusText.Text = $"{item.FileName}   ({bitmap.PixelWidth} x {bitmap.PixelHeight})";
    }

    private void ShowEmptyState(bool show)
    {
        EmptyStateText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            PreviewImage.Source = null;
            StatusText.Text = string.Empty;
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

    private void SelectRelative(int delta)
    {
        if (_thumbnails.Count == 0) return;
        var next = Math.Clamp(_currentIndex + delta, 0, _thumbnails.Count - 1);
        if (next == _currentIndex) return;
        ThumbnailList.SelectedIndex = next;
        ThumbnailList.ScrollIntoView(ThumbnailList.SelectedItem);
    }

    private void PrevImageButton_Click(object sender, RoutedEventArgs e) => SelectRelative(-1);

    private void NextImageButton_Click(object sender, RoutedEventArgs e) => SelectRelative(1);

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

        _isTreeCollapsed = collapsed;
    }

    // ---------- Zoom / pan ----------

    private void PreviewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => _panZoom.OnPreviewMouseWheel(e);

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
}
