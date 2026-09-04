using System.IO;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Services;

namespace ImageViewer;

/// <summary>Mutable preview row bound to the DataGrid - NewName starts out as whatever the
/// template produced but can be typed over directly, so a template-driven batch can still get a
/// one-off manual override without fighting the template.</summary>
public sealed class RenamePlanRow
{
    public required string OriginalPath { get; init; }
    public required string OriginalName { get; init; }
    public string NewName { get; set; } = "";
}

/// <summary>Batch-renames either the image files directly inside one folder, or that folder's
/// immediate subfolders, using a template with {name}/{ext}/{n} tokens (see BatchRenamer). Shows
/// an old-name → new-name preview - editable, so individual rows (including the {n}-derived
/// number) can be tweaked by hand - before anything is actually renamed.</summary>
public partial class BatchRenameWindow : Window
{
    private readonly string _folderPath;
    private List<RenamePlanRow>? _currentRows;

    public event Action? RenameApplied;

    public BatchRenameWindow(string folderPath)
    {
        InitializeComponent();
        _folderPath = folderPath;
        FolderPathText.Text = folderPath;
        UpdatePreview();
    }

    private RenameTargetKind SelectedKind => TargetFolderRadio.IsChecked == true ? RenameTargetKind.Folder : RenameTargetKind.File;

    private List<string> GetTargetPaths()
    {
        try
        {
            if (SelectedKind == RenameTargetKind.File)
                return Directory.EnumerateFiles(_folderPath)
                    .Where(f => ImageLoader.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return Directory.EnumerateDirectories(_folderPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private void Target_Checked(object sender, RoutedEventArgs e) => UpdatePreview();

    private void Template_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewGrid == null) return; // fires once during XAML init, before controls exist

        var paths = GetTargetPaths();
        var template = TemplateTextBox.Text;
        if (string.IsNullOrWhiteSpace(template)) template = "{name}";
        if (!int.TryParse(StartNumberTextBox.Text, out var start)) start = 1;

        var plan = BatchRenamer.BuildPlan(paths, SelectedKind, template, start);
        _currentRows = plan.Select(p => new RenamePlanRow
        {
            OriginalPath = p.OriginalPath,
            OriginalName = p.OriginalName,
            NewName = p.NewName
        }).ToList();

        PreviewGrid.ItemsSource = _currentRows;
        CountText.Text = $"{_currentRows.Count}개 항목";
        ApplyButton.IsEnabled = _currentRows.Count > 0;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRows == null || _currentRows.Count == 0) return;

        // Commit whatever cell is still being edited (e.g. Apply clicked right after typing a
        // manual override) before reading NewName back out.
        PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var plan = _currentRows
            .Select(r => new RenamePlanItem(r.OriginalPath, r.OriginalName, r.NewName))
            .ToList();

        var error = BatchRenamer.ValidatePlan(plan);
        if (error != null)
        {
            MessageBox.Show(this, error, "이름을 변경할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"{plan.Count}개 항목의 이름을 변경합니다. 되돌릴 수 없습니다. 계속할까요?",
            "일괄 이름 변경", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            BatchRenamer.ApplyPlan(plan, SelectedKind);
            MessageBox.Show(this, "이름 변경이 완료되었습니다.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Information);
            RenameApplied?.Invoke();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"이름 변경 중 오류가 발생했습니다:\n{ex.Message}", "Vista Mia",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
