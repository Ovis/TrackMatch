using System.Numerics;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// Segment SimHashのMulti-Index Hamming探索で詳細比較対象のTrackペアを抽出する。
/// </summary>
public sealed class CandidatePairGenerator(FingerprintSegmentSketcher sketcher)
{
    public CandidateGenerationResult Generate(
        IReadOnlyList<StoredFingerprint> fingerprints,
        CandidateGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var sketches = fingerprints
            .SelectMany(fingerprint => sketcher.Create(fingerprint, options))
            .ToArray();
        var pairs = GenerateFromSketches(sketches, targetTrackIds: null, options.MaximumSegmentHashHammingDistance);
        return new CandidateGenerationResult(fingerprints.Count, sketches.Length, pairs);
    }

    /// <summary>
    /// 保存済みSegment Sketchから候補ペアを生成する。
    /// </summary>
    /// <param name="sketches">現在有効な全TrackのSegment Sketch</param>
    /// <param name="targetTrackIds">指定した場合、このTrackを一方に含むペアだけを返す</param>
    /// <param name="maximumDistance">許容する32-bit SimHash Hamming距離</param>
    /// <param name="progress">探索済みSketch数を通知する進捗通知先</param>
    public IReadOnlyList<CandidatePair> GenerateFromSketches(
        IReadOnlyList<FingerprintSegmentSketch> sketches,
        IReadOnlySet<long>? targetTrackIds,
        int maximumDistance,
        IProgress<CandidatePairGenerationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(sketches);
        if (maximumDistance is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        }

        var lowerIndex = new Dictionary<ushort, List<FingerprintSegmentSketch>>();
        var upperIndex = new Dictionary<ushort, List<FingerprintSegmentSketch>>();
        var pairDistances = new Dictionary<(long A, long B), int>();
        var completed = 0;
        progress?.Report(new CandidatePairGenerationProgress(completed, sketches.Count));

        foreach (var sketch in sketches)
        {
            MatchHalf(sketch, unchecked((ushort)sketch.Hash), lowerIndex, pairDistances, targetTrackIds, maximumDistance);
            MatchHalf(sketch, unchecked((ushort)(sketch.Hash >> 16)), upperIndex, pairDistances, targetTrackIds, maximumDistance);

            AddToIndex(lowerIndex, unchecked((ushort)sketch.Hash), sketch);
            AddToIndex(upperIndex, unchecked((ushort)(sketch.Hash >> 16)), sketch);
            completed++;
            progress?.Report(new CandidatePairGenerationProgress(completed, sketches.Count));
        }

        return pairDistances
            .Select(item => new CandidatePair(item.Key.A, item.Key.B, item.Value))
            .OrderBy(pair => pair.TrackIdA)
            .ThenBy(pair => pair.TrackIdB)
            .ToArray();
    }

    private static void MatchHalf(
        FingerprintSegmentSketch current,
        ushort half,
        IReadOnlyDictionary<ushort, List<FingerprintSegmentSketch>> index,
        Dictionary<(long A, long B), int> pairDistances,
        IReadOnlySet<long>? targetTrackIds,
        int maximumDistance)
    {
        foreach (var neighbor in EnumerateDistanceOneNeighborhood(half))
        {
            if (!index.TryGetValue(neighbor, out var indexedSketches))
            {
                continue;
            }

            foreach (var other in indexedSketches)
            {
                if (other.TrackId == current.TrackId)
                {
                    continue;
                }

                if (targetTrackIds is not null
                    && !targetTrackIds.Contains(current.TrackId)
                    && !targetTrackIds.Contains(other.TrackId))
                {
                    continue;
                }

                var distance = BitOperations.PopCount(current.Hash ^ other.Hash);
                if (distance > maximumDistance)
                {
                    continue;
                }

                var key = current.TrackId < other.TrackId
                    ? (current.TrackId, other.TrackId)
                    : (other.TrackId, current.TrackId);
                if (!pairDistances.TryGetValue(key, out var bestDistance) || distance < bestDistance)
                {
                    pairDistances[key] = distance;
                }
            }
        }
    }

    private static IEnumerable<ushort> EnumerateDistanceOneNeighborhood(ushort value)
    {
        yield return value;
        for (var bit = 0; bit < 16; bit++)
        {
            yield return unchecked((ushort)(value ^ (1 << bit)));
        }
    }

    private static void AddToIndex(
        IDictionary<ushort, List<FingerprintSegmentSketch>> index,
        ushort key,
        FingerprintSegmentSketch sketch)
    {
        if (!index.TryGetValue(key, out var bucket))
        {
            bucket = [];
            index.Add(key, bucket);
        }

        bucket.Add(sketch);
    }
}

/// <summary>
/// 候補ペア探索の進捗を表す。
/// </summary>
public sealed record CandidatePairGenerationProgress(int CompletedSketches, int TotalSketches);
