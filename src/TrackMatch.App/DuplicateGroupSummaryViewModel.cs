using TrackMatch.Core.Duplicates;

namespace TrackMatch.App;

/// <summary>
/// Main Windowで現在の候補に関係するGlobal Duplicate GroupのLibrary Projectionを表示するモデル。
/// </summary>
public sealed record DuplicateGroupSummaryViewModel(
    long Id,
    int FileCount,
    int GlobalFileCount,
    long? KeepTrackId,
    DuplicateGroupKeepStatus KeepStatus,
    string KeepTitle,
    string? KeepYear)
{
    /// <summary>現在Libraryの構成件数を含む見出し。</summary>
    public string Header => $"重複グループ #{Id}（{FileCount}ファイル）";

    /// <summary>Keep状態をMain Windowの省スペース表示向けに整形する。</summary>
    public string KeepSummary => KeepStatus switch
    {
        DuplicateGroupKeepStatus.Selected when string.IsNullOrWhiteSpace(KeepYear) => KeepTitle,
        DuplicateGroupKeepStatus.Selected => $"{KeepTitle}（{KeepYear}）",
        DuplicateGroupKeepStatus.Conflict => "残すファイルが競合しています",
        DuplicateGroupKeepStatus.Missing => "残すファイルが見つかりません",
        _ => "残すファイルを選択してください",
    };

    /// <summary>現在Library内の残り件数とLibrary外へのGroup継続を示す。</summary>
    public string OtherFilesText
    {
        get
        {
            var local = FileCount > 1 ? $"他 {FileCount - 1} ファイル" : string.Empty;
            var externalCount = Math.Max(0, GlobalFileCount - FileCount);
            if (externalCount == 0)
            {
                return local;
            }

            return string.IsNullOrEmpty(local)
                ? $"Library外に {externalCount} ファイル"
                : $"{local} / Library外に {externalCount} ファイル";
        }
    }
}
