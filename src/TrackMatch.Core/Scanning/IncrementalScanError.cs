namespace TrackMatch.Core.Scanning;

/// <summary>
/// 増分走査中にファイル単位で発生したエラーを表す。
/// </summary>
public sealed record IncrementalScanError(
    string Path,
    string Stage,
    string Message);
