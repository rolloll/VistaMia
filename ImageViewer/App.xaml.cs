using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Windows;
using ImageViewer.Services;

namespace ImageViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Windows passes the target file as argv[0] for "Open with" / file-association launches.
        var initialFilePath = e.Args.Length > 0 ? e.Args[0] : null;
        var mainWindow = new MainWindow(initialFilePath);
        mainWindow.Show();

        _ = CheckForUpdatesAsync(mainWindow);
    }

    private static async Task CheckForUpdatesAsync(Window owner)
    {
        var update = await UpdateChecker.CheckForUpdateAsync();
        if (update == null) return;

        var result = MessageBox.Show(owner,
            $"새 버전 {update.Version}이 있습니다 (현재 버전: {UpdateChecker.CurrentVersion}).\nGitHub 릴리스 페이지로 이동해서 다운로드하시겠습니까?",
            "업데이트 확인", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
        }
    }
}

