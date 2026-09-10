namespace TrackMatch.Core.Candidates;

/// <summary>
/// Fingerprintの一定区間を32-bit SimHashへ圧縮した索引用Sketchを表す。
/// </summary>
public sealed record FingerprintSegmentSketch(
    long TrackId,
    int SegmentIndex,
    uint Hash);
