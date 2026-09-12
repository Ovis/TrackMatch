namespace TrackMatch.Core.Quality;

/// <summary>
/// 品質所見の重要度を表す。品質の総合点ではなく、利用者が確認すべき度合いだけを表現する。
/// </summary>
public enum QualityFindingSeverity
{
    Information,
    Reference,
    Caution,
    StrongCaution,
}

/// <summary>
/// 品質所見がどちらのTrackまたは比較全体に属するかを表す。
/// </summary>
public enum QualityFindingTarget
{
    Comparison,
    TrackA,
    TrackB,
    BothTracks,
}

/// <summary>
/// UI文言から独立した品質所見の種類を表す。
/// </summary>
public enum QualityFindingCode
{
    PrimarilyGainDifference,
    AdditionalDynamicsDifferencePossible,
    ClippingSuspicion,
    NearZeroTruePeak,
    HighFrequencyCutoff,
    ChannelImbalance,
}

/// <summary>
/// Candidate品質比較で検出された1件の構造化所見を保持する。
/// </summary>
public sealed record QualityFinding(
    QualityFindingCode Code,
    QualityFindingSeverity Severity,
    QualityFindingTarget Target,
    double? PrimaryValue = null,
    double? SecondaryValue = null);
