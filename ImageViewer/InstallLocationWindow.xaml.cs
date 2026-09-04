using System.IO;
using System.Windows;
using ImageViewer.Services;

namespace ImageViewer;

/// <summary>First-run "where should this live" prompt shown when the app isn't at a location the
/// user has already chosen (see AppInstallLocation). Skipping just re-prompts next launch.</summary>
public partial class InstallLocationWindow : Window
{
    public bool Installed { get; private set; }

    public InstallLocationWindow()
    {
        InitializeComponent();
        PathTextBox.Text = AppInstallLocation.DefaultDirectory;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Vista Mia를 설치할 폴더를 선택하세요",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var picked = dialog.SelectedPath.TrimEnd('\\', '/');
        var folderName = Path.GetFileName(picked);
        // A folder already named VistaMia is used as-is; anything else gets a VistaMia subfolder
        // so the app doesn't spill loose files into a shared parent folder.
        PathTextBox.Text = string.Equals(folderName, "VistaMia", StringComparison.OrdinalIgnoreCase)
            ? picked
            : Path.Combine(picked, "VistaMia");
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "설치 폴더를 입력하거나 선택하세요.", "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AppInstallLocation.InstallAndRelaunch(path))
        {
            Installed = true;
            Close();
        }
        else
        {
            MessageBox.Show(this, "설치에 실패했습니다. 다른 폴더를 선택하거나, 관리자 권한 요청 창에서 '예'를 눌러주세요.",
                "Vista Mia", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
