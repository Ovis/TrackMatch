namespace TrackMatch.Core.Libraries;

/// <summary>
/// Libraryに登録された1つの音楽ファイルRootを表す。
/// </summary>
public sealed record LibraryRoot(
    long Id,
    long LibraryId,
    string Path);
