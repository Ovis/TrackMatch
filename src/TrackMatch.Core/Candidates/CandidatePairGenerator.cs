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

        var lowerIndex = new Dictionary<ushort, List<FingerprintSegmentSketch>>();
        var upperIndex = new Dictionary<ushort, List<FingerprintSegmentSketch>>();
        var pairDistances = new Dictionary<(long A, long B), int>();
        var segmentCount = 0;

        foreach (var fingerprint in fingerprints)
        {
            foreach (var sketch in sketcher.Create(fingerprint, options))
            {
                segmentCount++;
                MatchHalf(sketch, unchecked((ushort)sketch.Hash), lowerIndex, pairDistances, options.MaximumSegmentHashHammingDistance);
                MatchHalf(sketch, unchecked((ushort)(sketch.Hash >> 16)), upperIndex, pairDistances, options.MaximumSegmentHashHammingDistance);

                AddToIndex(lowerIndex, unchecked((ushort)sketch.Hash), sketch);
                AddToIndex(upperIndex, unchecked((ushort)(sketch.Hash >> 16)), sketch);
            }
        }

        var pairs = pairDistances
            .Select(item => new CandidatePair(item.Key.A, item.Key.B, item.Value))
            .OrderBy(pair => pair.TrackIdA)
            .ThenBy(pair => pair.TrackIdB)
            .ToArray();
        return new CandidateGenerationResult(fingerprints.Count, segmentCount, pairs);
    }

    private static void MatchHalf(
        FingerprintSegmentSketch current,
        ushort half,
        IReadOnlyDictionary<ushort, List<FingerprintSegmentSketch>> index,
        Dictionary<(long A, long B), int> pairDistances,
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
