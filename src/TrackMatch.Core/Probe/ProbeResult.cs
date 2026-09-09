using TrackMatch.Core.Comparison;
using TrackMatch.Core.Fingerprinting;

namespace TrackMatch.Core.Probe;

/// <summary>
/// 既知ペアに対するProbe結果を表す。
/// </summary>
public sealed record ProbeResult(
    ProbePair Pair,
    AudioFingerprint FingerprintA,
    AudioFingerprint FingerprintB,
    FingerprintComparisonResult Comparison)
{
    public double DurationRatio =>
        Math.Min(FingerprintA.Duration.TotalSeconds, FingerprintB.Duration.TotalSeconds)
        / Math.Max(FingerprintA.Duration.TotalSeconds, FingerprintB.Duration.TotalSeconds);
}
