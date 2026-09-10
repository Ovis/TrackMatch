namespace TrackMatch.Core.Candidates;

/// <summary>
/// Fingerprintから比較候補を抽出するためのパラメータを表す。
/// </summary>
public sealed record CandidateGenerationOptions
{
    /// <summary>
    /// 1区間に含めるChromaprint item数。Algorithm 2では256 itemがおよそ32秒に相当する。
    /// </summary>
    public int SegmentLengthItems { get; init; } = 256;

    /// <summary>
    /// 隣接区間の開始位置間隔。半区間ずらすことで開始位置差に対する取りこぼしを抑える。
    /// </summary>
    public int SegmentStrideItems { get; init; } = 128;

    /// <summary>
    /// 32-bit segment SimHashで候補とみなす最大Hamming距離。
    /// </summary>
    public int MaximumSegmentHashHammingDistance { get; init; } = 3;

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
    }
}
