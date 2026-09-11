namespace TrackMatch.Core.Libraries;

/// <summary>
/// Root保存場所変更前の検証結果を保持する。
/// </summary>
public sealed record LibraryRootRemapPreview(
    long LibraryId,
    long RootId,
    string OldRootPath,
    string NewRootPath,
    int TargetTrackCount,
    int MatchedTrackCount,
    IReadOnlyList<string> MissingRelativePaths,
    int UnknownAudioFileCount)
{
    /// <summary>
    /// 新Rootで見つからなかった既存Track数を返す。
    /// </summary>
    public int MissingTrackCount => MissingRelativePaths.Count;
}

/// <summary>
/// Root保存場所変更を適用した結果を保持する。
/// </summary>
public sealed record LibraryRootRemapResult(
    long LibraryId,
    long RootId,
    string OldRootPath,
    string NewRootPath,
    int TargetTrackCount,
    int MatchedTrackCount,
    int MissingTrackCount,
    int UnknownAudioFileCount);
