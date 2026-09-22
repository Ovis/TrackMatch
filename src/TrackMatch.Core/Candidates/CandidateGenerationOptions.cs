namespace TrackMatch.Core.Candidates;

/// <summary>
/// Fingerprintから比較候補を抽出するためのパラメータを表す。
/// </summary>
public sealed record CandidateGenerationOptions
{
    /// <summary>
    /// 1区間に含めるChromaprint item数。
    /// </summary>
    public int SegmentLengthItems { get; init; } = 128;

    /// <summary>
    /// 隣接区間の開始位置間隔。
    /// </summary>
    public int SegmentStrideItems { get; init; } = 64;

    /// <summary>
    /// 32-bit segment SimHashでsegment matchとみなす最大Hamming距離。
    /// </summary>
    public int MaximumSegmentHashHammingDistance { get; init; } = 3;

    /// <summary>
    /// 同じsegment offsetでCandidate成立に必要な最小hit数。
    /// </summary>
    public int MinimumDominantOffsetHits { get; init; } = 3;

    /// <summary>
    /// 設定値がCandidate Generationで利用可能か検証する。
    /// </summary>
    public void Validate()
    {
        if (SegmentLengthItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentLengthItems));
        }

        if (SegmentStrideItems <= 0 || SegmentStrideItems > SegmentLengthItems)
        {
            throw new ArgumentOutOfRangeException(nameof(SegmentStrideItems));
        }

        // 16-bit×2のMulti-Indexで半分側の距離1までを列挙するため、距離3以下なら候補漏れなく探索できる。
        if (MaximumSegmentHashHammingDistance is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSegmentHashHammingDistance));
        }

        if (MinimumDominantOffsetHits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumDominantOffsetHits));
        }
    }
}
