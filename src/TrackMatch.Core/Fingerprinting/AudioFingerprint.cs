namespace TrackMatch.Core.Fingerprinting;

/// <summary>
/// 音源から生成したChromaprintのraw fingerprintを表す。
/// </summary>
public sealed record AudioFingerprint(
    string Path,
    TimeSpan Duration,
    IReadOnlyList<uint> Values);
