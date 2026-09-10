namespace TrackMatch.Core.Candidates;

/// <summary>
/// 順序に依存しないTrackペア識別子を表す。
/// </summary>
public readonly record struct CandidatePairKey(long TrackIdA, long TrackIdB)
{
    public static CandidatePairKey Create(long trackIdA, long trackIdB)
    {
        if (trackIdA <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIdA));
        }

        if (trackIdB <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackIdB));
        }

        if (trackIdA == trackIdB)
        {
            throw new ArgumentException("同一Track同士はペアにできない。");
        }

        return trackIdA < trackIdB
            ? new CandidatePairKey(trackIdA, trackIdB)
            : new CandidatePairKey(trackIdB, trackIdA);
    }
}
