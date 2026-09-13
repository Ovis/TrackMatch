using System.Text.RegularExpressions;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Library名とWindows Root Pathを永続化前に正規化する。
/// </summary>
internal static partial class LibraryValueNormalizer
{
    /// <summary>
    /// 表示用Library名から比較用キーを生成する。
    /// </summary>
    internal static (string DisplayName, string Key) NormalizeLibraryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var displayName = WhitespaceRegex().Replace(name.Trim(), " ");
        if (displayName.Length == 0)
        {
            throw new ArgumentException("ライブラリ名には空白以外の文字が必要です。", nameof(name));
        }

        return (displayName, displayName.ToUpperInvariant());
    }

    /// <summary>
    /// Windowsの絶対Pathを、比較と保存に使用できる字句上の正規形へ変換する。
    /// </summary>
    /// <remarks>
    /// JunctionやSymlinkの実体解決は仕様対象外なので、ファイルシステムへ問い合わせず字句だけを正規化する。
    /// この実装によりLinux上のCIでもWindows Path規則を同じように検証できる。
    /// </remarks>
    internal static (string DisplayPath, string Key) NormalizeRootPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var value = path.Trim().Replace('/', '\\');
        string prefix;
        string remainder;

        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var parts = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new ArgumentException("UNCパスにはサーバー名と共有名が必要です。", nameof(path));
            }

            prefix = $"\\\\{parts[0]}\\{parts[1]}";
            remainder = string.Join('\\', parts.Skip(2));
        }
        else if (value.Length >= 3
                 && char.IsAsciiLetter(value[0])
                 && value[1] == ':'
                 && value[2] == '\\')
        {
            prefix = $"{char.ToUpperInvariant(value[0])}:";
            remainder = value[3..];
        }
        else
        {
            throw new ArgumentException("対象フォルダにはWindowsの絶対パスを指定してください。", nameof(path));
        }

        var normalizedSegments = new List<string>();
        foreach (var segment in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (normalizedSegments.Count == 0)
                {
                    throw new ArgumentException("対象フォルダより上位へ移動するパスは指定できません。", nameof(path));
                }

                normalizedSegments.RemoveAt(normalizedSegments.Count - 1);
                continue;
            }

            normalizedSegments.Add(segment);
        }

        var displayPath = prefix.StartsWith("\\\\", StringComparison.Ordinal)
            ? prefix + (normalizedSegments.Count == 0 ? string.Empty : "\\" + string.Join('\\', normalizedSegments))
            : prefix + "\\" + string.Join('\\', normalizedSegments);

        // Windowsの通常Path比較に合わせ、大小文字差を比較キーで吸収する。
        return (displayPath, displayPath.ToUpperInvariant());
    }

    /// <summary>
    /// 2つの正規化Rootが同一または包含関係にあるかを判定する。
    /// </summary>
    internal static bool Overlaps(string leftKey, string rightKey)
        => string.Equals(leftKey, rightKey, StringComparison.Ordinal)
           || IsAncestor(leftKey, rightKey)
           || IsAncestor(rightKey, leftKey);

    private static bool IsAncestor(string ancestor, string descendant)
        => descendant.Length > ancestor.Length
           && descendant.StartsWith(ancestor, StringComparison.Ordinal)
           && (ancestor.EndsWith('\\') || descendant[ancestor.Length] == '\\');

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
