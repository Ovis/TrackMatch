using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// 増分ライブラリ走査1回分の結果を表す。
/// </summary>
public sealed record IncrementalScanResult(
    long SessionId,
    ScanSessionSummary Summary,
    IReadOnlyList<IncrementalScanError> Errors,
    IReadOnlyList<ContentChangeNotice>? ContentChanges = null);

/// <summary>
/// Audio Content Change確定によって解除されたHuman Verdict件数を通知する。
/// </summary>
public sealed record ContentChangeNotice(string Path, int InvalidatedReviewCount);
