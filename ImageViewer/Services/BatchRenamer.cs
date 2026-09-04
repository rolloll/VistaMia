using System.IO;
using System.Text.RegularExpressions;

namespace ImageViewer.Services;

public enum RenameTargetKind
{
    File,
    Folder
}

public sealed record RenamePlanItem(string OriginalPath, string OriginalName, string NewName)
{
    public string NewPath => Path.Combine(Path.GetDirectoryName(OriginalPath)!, NewName);
}

/// <summary>
/// Builds and applies batch rename plans for files or folders inside one directory, using a
/// template with {name} (original name without extension), {ext} (file extension, without the
/// dot) and {n} / {n:3} (sequence number, optionally zero-padded to N digits) tokens. The
/// sequence starts at a caller-supplied number and increments by 1 per item, in list order.
/// </summary>
public static class BatchRenamer
{
    private static readonly Regex SequenceTokenPattern = new(@"\{n(?::(\d+))?\}", RegexOptions.Compiled);

    public static List<RenamePlanItem> BuildPlan(IReadOnlyList<string> paths, RenameTargetKind kind, string template, int startNumber)
    {
        var plan = new List<RenamePlanItem>();
        var seq = startNumber;

        foreach (var path in paths)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var originalName = Path.GetFileName(trimmed);
            var baseName = kind == RenameTargetKind.File ? Path.GetFileNameWithoutExtension(trimmed) : originalName;
            var ext = kind == RenameTargetKind.File ? Path.GetExtension(trimmed) : string.Empty;

            var name = template.Replace("{name}", baseName).Replace("{ext}", ext.TrimStart('.'));
            name = SequenceTokenPattern.Replace(name, m =>
            {
                var width = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
                return width > 0 ? seq.ToString().PadLeft(width, '0') : seq.ToString();
            });

            // Files keep their original extension unless the template explicitly places {ext}.
            if (kind == RenameTargetKind.File && !template.Contains("{ext}") && !string.IsNullOrEmpty(ext))
                name += ext;

            plan.Add(new RenamePlanItem(trimmed, originalName, name));
            seq++;
        }

        return plan;
    }

    /// <returns>An error message if the plan can't be applied as-is, or null if it's valid.</returns>
    public static string? ValidatePlan(List<RenamePlanItem> plan)
    {
        var seenNewPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan)
        {
            if (string.IsNullOrWhiteSpace(item.NewName))
                return $"'{item.OriginalName}'의 새 이름이 비어 있습니다.";
            if (item.NewName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return $"'{item.NewName}'에 파일/폴더 이름에 쓸 수 없는 문자가 있습니다.";
            if (!seenNewPaths.Add(item.NewPath))
                return $"이름이 중복됩니다: '{item.NewName}'";
        }
        return null;
    }

    /// <summary>Applies the plan. Each rename goes through a temporary name first, so a plan that
    /// shifts names around in a cycle (or only changes case) never collides with an original name
    /// that hasn't been renamed yet.</summary>
    public static void ApplyPlan(List<RenamePlanItem> plan, RenameTargetKind kind)
    {
        var pending = new List<(string TempPath, RenamePlanItem Item)>();

        foreach (var item in plan)
        {
            if (string.Equals(item.OriginalPath, item.NewPath, StringComparison.Ordinal))
                continue;

            var dir = Path.GetDirectoryName(item.OriginalPath)!;
            var tempPath = Path.Combine(dir, ".vistamia_tmp_" + Guid.NewGuid().ToString("N"));
            Move(item.OriginalPath, tempPath, kind);
            pending.Add((tempPath, item));
        }

        foreach (var (tempPath, item) in pending)
            Move(tempPath, item.NewPath, kind);
    }

    private static void Move(string from, string to, RenameTargetKind kind)
    {
        if (kind == RenameTargetKind.File) File.Move(from, to);
        else Directory.Move(from, to);
    }
}
