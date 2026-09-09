namespace TrackMatch.Core.Fingerprinting;

/// <summary>
/// 音源ファイルから音響フィンガープリントを生成する。
/// </summary>
public interface IFingerprintExtractor
{
    Task<AudioFingerprint> ExtractAsync(string path, CancellationToken cancellationToken = default);
}
