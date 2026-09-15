namespace TrackMatch.Core.Management;

/// <summary>
/// Global Track管理画面で使用する表示Filterを表す。
/// </summary>
public enum TrackManagementFilter
{
    All,
    Missing,
    Unowned,
}

/// <summary>
/// Track管理画面に表示するGlobal Track 1件の状態を表す。
/// </summary>
public sealed record ManagedTrack(
    long TrackId,
    string Path,
    string Title,
    string Artist,
    bool IsMissing,
    IReadOnlyList<string> LibraryNames)
{
    /// <summary>どのLibraryからも参照されていないGlobal Trackかを返す。</summary>
    public bool IsUnowned => LibraryNames.Count == 0;

    /// <summary>Library Membershipを確認画面向けに整形する。</summary>
    public string LibrariesText => LibraryNames.Count == 0 ? "未所属" : string.Join(", ", LibraryNames);

    /// <summary>Missing状態を短い表示文字列へ変換する。</summary>
    public string StateText => IsMissing ? "Missing" : IsUnowned ? "未所属" : "利用可能";
}

/// <summary>
/// Force Reanalysisの対象範囲を表す。
/// </summary>
public enum ForceReanalysisScope
{
    Track,
    Root,
    Library,
}

/// <summary>
/// Force Reanalysisで無効化したGlobal Track件数を表す。
/// </summary>
public sealed record ForceReanalysisResult(
    ForceReanalysisScope Scope,
    int TrackCount,
    int ArchivedReviewCount);
