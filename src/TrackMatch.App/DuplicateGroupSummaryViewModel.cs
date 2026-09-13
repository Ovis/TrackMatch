namespace TrackMatch.App;

/// <summary>
/// Main Windowで現在の候補に関係する重複グループをコンパクトに表示するためのモデル。
/// </summary>
public sealed record DuplicateGroupSummaryViewModel(
    long Id,
    int FileCount,
    long KeepTrackId,
    string KeepTitle,
    string? KeepYear)
{
    public string Header => $"重複グループ #{Id}（{FileCount}ファイル）";
    public string KeepSummary => string.IsNullOrWhiteSpace(KeepYear)
        ? KeepTitle
        : $"{KeepTitle}（{KeepYear}）";
    public string OtherFilesText => FileCount > 1 ? $"他 {FileCount - 1} ファイル" : string.Empty;
}
