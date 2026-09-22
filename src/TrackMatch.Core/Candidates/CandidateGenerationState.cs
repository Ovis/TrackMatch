namespace TrackMatch.Core.Candidates;

/// <summary>
/// LibraryのCandidatePairsが最後に正常生成された構成を表す。
/// </summary>
public sealed record CandidateGenerationState(
    int CandidateGenerationAlgorithmVersion,
    int FingerprintAlgorithm,
    int SegmentLengthItems,
    int SegmentStrideItems,
    int MaximumSegmentHashHammingDistance,
    int MinimumDominantOffsetHits)
{
    /// <summary>
    /// 現在の実行構成に対応する完了状態を作成する。
    /// </summary>
    public static CandidateGenerationState Create(int fingerprintAlgorithm, CandidateGenerationOptions options)
        => new(
            CandidateGenerationVersion.Current,
            fingerprintAlgorithm,
            options.SegmentLengthItems,
            options.SegmentStrideItems,
            options.MaximumSegmentHashHammingDistance,
            options.MinimumDominantOffsetHits);
}
