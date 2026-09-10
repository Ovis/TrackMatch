using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// 増分ライブラリ走査1回分の結果を表す。
/// </summary>
public sealed record IncrementalScanResult(
    long SessionId,
    ScanSessionSummary Summary,
    IReadOnlyList<IncrementalScanError> Errors);
