using System.Diagnostics;
using System.IO;
using System.Text;

namespace ImageViewer.Services;

/// <summary>
/// Lets the user choose where Vista Mia lives on first run (instead of just running out of
/// wherever the GitHub release zip was extracted), and remembers that choice so later launches -
/// and self-updates - go straight there. "VistaMia\install-location.txt" under the user's AppData
/// is the record of that choice; its absence means this machine hasn't installed yet.
/// </summary>
public static class AppInstallLocation
{
    /// <summary>Suggested default shown in the install-location prompt.</summary>
    public static readonly string DefaultDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VistaMia");

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VistaMia", "install-location.txt");

    /// <summary>The folder the user previously chose to install into, or null if this machine has
    /// never completed the install-location prompt.</summary>
    public static string? GetInstalledDirectory()
    {
        try
        {
            return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRunningFromInstalledLocation()
    {
        var currentDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var installed = GetInstalledDirectory();
        if (installed != null)
            return string.Equals(currentDir, installed.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        // No recorded choice yet. If this happens to already be running from the old fixed
        // default (from before users could pick their own folder), treat that as installed
        // rather than prompting someone who's already set up.
        if (string.Equals(currentDir, DefaultDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            SaveInstalledDirectory(currentDir);
            return true;
        }

        return false;
    }

    private static void SaveInstalledDirectory(string dir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, dir);
    }

    /// <summary>Probes whether a directory can be written to by the current (non-elevated)
    /// process, so callers only ask for UAC when the chosen folder actually needs it (e.g. under
    /// Program Files) rather than for every install/update regardless of where it lives.</summary>
    public static bool CanWriteWithoutElevation(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probePath = Path.Combine(dir, ".vistamia_write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the running app's files into <paramref name="targetDir"/>, remembers the choice,
    /// and relaunches from there. Tries a plain copy first; if that's denied (e.g. the user picked
    /// somewhere under Program Files), falls back to a UAC-elevated PowerShell copy.
    /// </summary>
    /// <returns>true if a relaunch was scheduled - the caller should shut down immediately.</returns>
    public static bool InstallAndRelaunch(string targetDir)
    {
        var currentDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        targetDir = targetDir.TrimEnd('\\', '/');
        var targetExe = Path.Combine(targetDir, "VistaMia.exe");

        if (CanWriteWithoutElevation(targetDir))
        {
            try
            {
                CopyAppFiles(currentDir, targetDir);
                SaveInstalledDirectory(targetDir);
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{targetExe}\"" });
                return true;
            }
            catch
            {
                // Fall through to the elevated path below - some other reason the plain copy
                // failed (a locked file, etc.) rather than a permissions issue.
            }
        }

        return InstallElevated(currentDir, targetDir, targetExe);
    }

    private static bool InstallElevated(string currentDir, string targetDir, string targetExe)
    {
        try
        {
            // Same .ps1-over-.bat reasoning as SelfUpdater: Korean path segments survive a
            // UTF-8-BOM PowerShell script but get mangled by cmd.exe's codepage guessing.
            var scriptPath = Path.Combine(Path.GetTempPath(), "VistaMiaInstall_" + Guid.NewGuid().ToString("N") + ".ps1");
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                New-Item -ItemType Directory -Path "{{targetDir}}" -Force | Out-Null
                Copy-Item -Path "{{currentDir}}\*" -Destination "{{targetDir}}" -Recurse -Force
                Remove-Item -Path "{{targetDir}}\win-x64" -Recurse -Force -ErrorAction SilentlyContinue
                Start-Process -FilePath explorer.exe -ArgumentList "{{targetExe}}"
                """;
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas"
            });

            SaveInstalledDirectory(targetDir);
            return true;
        }
        catch
        {
            // Most commonly a Win32Exception from the user declining the UAC prompt.
            return false;
        }
    }

    /// <summary>Recursively copies the app's own files, skipping "win-x64" - a stray nested
    /// publish-output folder that sometimes ends up inside the framework-dependent build output;
    /// it's dev-time clutter, not app content.</summary>
    private static void CopyAppFiles(string sourceDir, string destDir)
    {
        var skip = Path.Combine(sourceDir, "win-x64");

        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (dir.StartsWith(skip, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(skip, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
        }
    }
}
