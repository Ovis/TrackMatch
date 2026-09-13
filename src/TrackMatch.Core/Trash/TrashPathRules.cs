namespace TrackMatch.Core.Trash;

/// <summary>
/// Trash RootとLibrary Rootの配置、および元Audio FileからTrash内DestinationへのPath変換規則を提供する。
/// </summary>
public static class TrashPathRules
{
    /// <summary>
    /// Trash RootとLibrary Root群が同一・包含関係になっていないことを検証する。
    /// </summary>
    public static void ValidateRootSeparation(string trashRoot, IEnumerable<string> libraryRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        ArgumentNullException.ThrowIfNull(libraryRoots);

        var trash = Normalize(trashRoot);
        foreach (var libraryRoot in libraryRoots)
        {
            var root = Normalize(libraryRoot);
            if (IsSameOrUnder(trash, root) || IsSameOrUnder(root, trash))
            {
                throw new ArgumentException(
                    $"ごみ箱フォルダとライブラリの対象フォルダは、同じ場所または包含関係にはできません: {root}",
                    nameof(trashRoot));
            }
        }
    }

    /// <summary>
    /// 元Absolute Path構造を維持したTrash内Destination Pathを作成する。
    /// </summary>
    public static string CreateDestinationPath(string trashRoot, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var trash = Normalize(trashRoot);
        var source = Path.GetFullPath(sourcePath);
        string relative;

        if (source.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var unc = source.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            relative = Path.Combine(["UNC", .. unc]);
        }
        else
        {
            var root = Path.GetPathRoot(source);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            {
                throw new ArgumentException("Windowsの絶対パスとして解釈できません。", nameof(sourcePath));
            }

            var drive = char.ToUpperInvariant(root[0]).ToString();
            var remainder = source[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            relative = string.IsNullOrEmpty(remainder) ? drive : Path.Combine(drive, remainder);
        }

        return Path.GetFullPath(Path.Combine(trash, relative));
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

/// <summary>
/// Trash Destinationが既に存在する場合の処理方針を表す。
/// </summary>
public enum TrashDestinationCollisionBehavior
{
    Skip,
    Rename,
}
