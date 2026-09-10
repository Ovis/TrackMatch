using TrackMatch.Core.Fingerprinting;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Track IDとFingerprintを候補生成用にまとめた永続化モデルを表す。
/// </summary>
public sealed record StoredFingerprint(
    long TrackId,
    int Algorithm,
    AudioFingerprint Fingerprint);
