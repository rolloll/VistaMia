using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace ImageViewer.Services;

/// <summary>
/// Downloads a release zip, extracts VistaMia.exe from it, and swaps it in for the currently
/// running exe so no old version is left behind. Since Windows won't let a running process
/// overwrite its own exe, the actual swap happens in a small detached PowerShell script that
/// waits for this process to exit first, then copies the new exe over and relaunches it.
///
/// This uses a .ps1 script rather than a .bat one: cmd.exe interprets a batch file's bytes using
/// whatever ANSI codepage is active when it starts reading, so install paths containing non-ASCII
/// characters (Korean, etc.) got silently mangled even with a `chcp 65001` line at the top -
/// PowerShell instead auto-detects the script's encoding from its BOM, so writing this file with
/// an explicit UTF-8 BOM sidesteps the codepage guesswork entirely.
/// </summary>
public static class SelfUpdater
{
    private const string ExeName = "VistaMia.exe";

    /// <returns>true if the update was downloaded and a relaunch was scheduled - the caller should
    /// shut down immediately after this returns true so the swap script can proceed.</returns>
    public static async Task<bool> DownloadAndApplyAsync(string assetDownloadUrl)
    {
        try
        {
            var currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExePath)) return false;

            var tempDir = Path.Combine(Path.GetTempPath(), "VistaMiaUpdate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var zipPath = Path.Combine(tempDir, "update.zip");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("VistaMia-Updater");
                var bytes = await client.GetByteArrayAsync(assetDownloadUrl);
                if (bytes.Length < 1024) return false; // suspiciously small - not a real build
                await File.WriteAllBytesAsync(zipPath, bytes);
            }

            var extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var newExePath = Directory.EnumerateFiles(extractDir, ExeName, SearchOption.AllDirectories).FirstOrDefault();
            if (newExePath == null) return false;

            // $$""" ... """ so PowerShell's single-brace `{ }` block below is left alone as
            // literal content; only the doubled {{ }} placeholders are C# interpolation.
            var scriptPath = Path.Combine(tempDir, "apply_update.ps1");
            var script = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                while (Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue) {
                    Start-Sleep -Milliseconds 500
                }
                Copy-Item -Path "{{newExePath}}" -Destination "{{currentExePath}}" -Force
                Remove-Item -Path "{{zipPath}}" -Force
                Remove-Item -Path "{{extractDir}}" -Recurse -Force
                Start-Process -FilePath explorer.exe -ArgumentList "{{currentExePath}}"
                """;
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true));

            // The install folder (see AppInstallLocation) is user-chosen and usually doesn't need
            // admin rights, but someone who picked somewhere under Program Files does need
            // elevation to write there - so probe first rather than always prompting for UAC.
            // UseShellExecute+Verb=runas can't be combined with CreateNoWindow, but "-WindowStyle
            // Hidden" is a PowerShell argument (not a launcher property), so either way the
            // script's own window stays hidden.
            var installDir = Path.GetDirectoryName(currentExePath)!;
            if (AppInstallLocation.CanWriteWithoutElevation(installDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
