namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補索引の更新判定に必要なFingerprintの状態を表す。
/// </summary>
public sealed record StoredFingerprintState(
    long TrackId,
    int Algorithm,
    DateTime ExtractedAtUtc);
