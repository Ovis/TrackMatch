namespace TrackMatch.Core.Libraries;

/// <summary>
/// Library削除前の確認表示に必要な管理データ件数を表す。
/// </summary>
public sealed record LibraryDeleteSummary(long RootCount, long TrackCount);
