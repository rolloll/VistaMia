using System.IO;
using System.Reflection;
using System.Text;

namespace ImageViewer.Services;

/// <summary>
/// Removes leftovers of older Vista Mia versions after an install or update, so installing the
/// latest GitHub release doesn't leave the previous version lying around.
///
/// It only ever deletes files this app itself ships (an explicit allow-list, never a wildcard
/// over a folder), and never touches the exe that is currently running. Two kinds of leftovers:
///  - "sidecar" files from the older framework-dependent layout (VistaMia.dll, Magick.NET*.dll,
///    runtimes\ ...). The current release is a self-contained single-file exe that carries all of
///    these inside itself, so next to it they're just stale copies of an old version.
///  - a whole previous install folder, when the user re-installs into a different location.
/// </summary>
public static class OldVersionCleaner
{
    private static readonly string[] SidecarFiles =
    {
        "VistaMia.dll", "VistaMia.pdb", "VistaMia.deps.json", "VistaMia.runtimeconfig.json",
        "Magick.NET-Q8-AnyCPU.dll", "Magick.NET.Core.dll", "Microsoft.Data.Sqlite.dll",
        "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll"
    };

    private static readonly string[] SidecarDirs = { "runtimes", "win-x64" };

    private const string ExeName = "VistaMia.exe";

    /// <summary>True when running as the self-contained single-file build. A framework-dependent
    /// build (Debug, or an old release) needs its sidecar files, so those are never cleaned.</summary>
    public static bool IsSingleFileBuild =>
        string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    /// <summary>Best-effort cleanup of stale sidecar files next to the running exe. Silently does
    /// nothing if the folder needs admin rights - SelfUpdater's elevated swap script handles that
    /// case during an update.</summary>
    public static void TryCleanCurrentInstall()
    {
        try
        {
            if (!IsSingleFileBuild) return;
            RemoveSidecars(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        }
        catch
        {
            // Locked or access-denied - leave it; it's only clutter.
        }
    }

    public static void RemoveSidecars(string dir)
    {
        foreach (var name in SidecarFiles)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var name in SidecarDirs)
        {
            var path = Path.Combine(dir, name);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Removes a previous install folder's app files, then the folder itself if that
    /// leaves it empty. Does nothing unless the folder really contains VistaMia.exe.</summary>
    public static void RemoveOldInstall(string dir)
    {
        if (!File.Exists(Path.Combine(dir, ExeName))) return;

        RemoveSidecars(dir);
        File.Delete(Path.Combine(dir, ExeName));
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    // ---- PowerShell equivalents, for the scripts that run detached/elevated ----------------

    /// <summary>PowerShell lines equivalent to <see cref="RemoveSidecars"/> (errors ignored).</summary>
    public static string SidecarCleanupScript(string dir)
    {
        var sb = new StringBuilder();
        foreach (var name in SidecarFiles)
            sb.AppendLine($"Remove-Item -LiteralPath '{Quote(Path.Combine(dir, name))}' -Force -ErrorAction SilentlyContinue");
        foreach (var name in SidecarDirs)
            sb.AppendLine($"Remove-Item -LiteralPath '{Quote(Path.Combine(dir, name))}' -Recurse -Force -ErrorAction SilentlyContinue");
        return sb.ToString();
    }

    /// <summary>PowerShell lines equivalent to <see cref="RemoveOldInstall"/>.</summary>
    public static string OldInstallRemovalScript(string dir)
    {
        var exe = Quote(Path.Combine(dir, ExeName));
        var d = Quote(dir);
        return $$"""
            if (Test-Path -LiteralPath '{{exe}}') {
            {{SidecarCleanupScript(dir)}}
                Remove-Item -LiteralPath '{{exe}}' -Force -ErrorAction SilentlyContinue
                if (-not (Get-ChildItem -LiteralPath '{{d}}' -Force -ErrorAction SilentlyContinue)) {
                    Remove-Item -LiteralPath '{{d}}' -Force -ErrorAction SilentlyContinue
                }
            }
            """;
    }

    private static string Quote(string s) => s.Replace("'", "''");
}
