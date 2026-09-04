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

#if !DEBUG
        // Keep this guarded to Release builds only - enforcing it in Debug would mean every F5 in
        // Visual Studio pops this prompt instead of just running the copy being debugged.
        if (!AppInstallLocation.IsRunningFromInstalledLocation())
        {
            var installWindow = new InstallLocationWindow();
            installWindow.ShowDialog();
            if (installWindow.Installed)
            {
                Shutdown();
                return;
            }
            // Skipped ("나중에") or the copy failed - keep running in place; this prompts again
            // next launch since no install location got recorded.
        }
#endif

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
            $"새 버전 {update.Version}이 있습니다 (현재 버전: {UpdateChecker.CurrentVersion}).\n지금 다운로드해서 자동으로 업데이트하시겠습니까?",
            "업데이트 확인", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes) return;

        if (update.AssetDownloadUrl == null)
        {
            // No matching release asset (e.g. a draft/manual release) - fall back to the browser.
            Process.Start(new ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
            return;
        }

        var progress = new UpdateProgressWindow { Owner = owner };
        progress.Show();

        var applied = await SelfUpdater.DownloadAndApplyAsync(update.AssetDownloadUrl);

        if (!applied)
        {
            progress.Close();
            MessageBox.Show(owner, "자동 업데이트에 실패했습니다. 대신 릴리스 페이지를 엽니다.", "업데이트 확인",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Process.Start(new ProcessStartInfo(update.ReleaseUrl) { UseShellExecute = true });
            return;
        }

        // The swap script is waiting for this process to exit before it copies the new exe in
        // and relaunches it, so shut down now rather than closing the progress window.
        Current.Shutdown();
    }
}

