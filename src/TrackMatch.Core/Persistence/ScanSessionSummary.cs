namespace TrackMatch.Core.Persistence;

/// <summary>
/// ライブラリ走査1回分の結果件数を表す。
/// </summary>
public sealed record ScanSessionSummary(
    int TotalFiles,
    int ProcessedFiles,
    int AddedFiles,
    int UpdatedFiles,
    int RemovedFiles,
    int ErrorCount);
