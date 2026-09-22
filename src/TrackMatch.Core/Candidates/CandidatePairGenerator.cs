using System.Numerics;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// Segment SimHashのMulti-Index Hamming探索で詳細比較対象のTrackペアを抽出する。
/// </summary>
public sealed class CandidatePairGenerator(FingerprintSegmentSketcher sketcher)
{
    /// <summary>
    /// FingerprintからSegment Sketchを生成してCandidate Pairを抽出する。
    /// </summary>
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
        var pairs = GenerateFromSketches(sketches, targetTrackIds: null, options);
        return new CandidateGenerationResult(fingerprints.Count, sketches.Length, pairs);
    }

    /// <summary>
    /// 保存済みSegment Sketchから、同一offsetに十分なhitがあるCandidate Pairを生成する。
    /// </summary>
    /// <param name="sketches">現在有効な全TrackのSegment Sketch</param>
    /// <param name="targetTrackIds">指定した場合、このTrackを一方に含むペアだけを返す</param>
    /// <param name="options">Candidate判定設定</param>
    /// <param name="progress">探索済みSketch数を通知する進捗通知先</param>
    public IReadOnlyList<CandidatePair> GenerateFromSketches(
        IReadOnlyList<FingerprintSegmentSketch> sketches,
        IReadOnlySet<long>? targetTrackIds,
        CandidateGenerationOptions options,
        IProgress<CandidatePairGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sketches);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var lowerIndex = new Dictionary<ushort, List<FingerprintSegmentSketch>>();
        var upperIndex = new Dictionary<ushort, List<FingerprintSegmentSketch>>();
        var evidence = new Dictionary<CandidatePairKey, Dictionary<int, OffsetEvidence>>();
        var completed = 0;
        progress?.Report(new CandidatePairGenerationProgress(completed, sketches.Count));

        foreach (var sketch in sketches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MatchHalf(sketch, unchecked((ushort)sketch.Hash), lowerIndex, evidence, targetTrackIds, options.MaximumSegmentHashHammingDistance, skipWhenLowerHalfAlreadyMatched: false, cancellationToken);
            MatchHalf(sketch, unchecked((ushort)(sketch.Hash >> 16)), upperIndex, evidence, targetTrackIds, options.MaximumSegmentHashHammingDistance, skipWhenLowerHalfAlreadyMatched: true, cancellationToken);

            AddToIndex(lowerIndex, unchecked((ushort)sketch.Hash), sketch);
            AddToIndex(upperIndex, unchecked((ushort)(sketch.Hash >> 16)), sketch);
            completed++;
            // 大規模Libraryでは数十万Sketchになるため、1件ごとの通知でUIキューを埋めない。
            // 最終件は必ず通知しつつ、途中経過は256件単位に抑える。
            if (completed == sketches.Count || (completed & 0xff) == 0)
            {
                progress?.Report(new CandidatePairGenerationProgress(completed, sketches.Count));
            }
        }

        return evidence
            .Select(item => CreateCandidate(item.Key, item.Value, options.MinimumDominantOffsetHits))
            .Where(pair => pair is not null)
            .Select(pair => pair!)
            .OrderBy(pair => pair.TrackIdA)
            .ThenBy(pair => pair.TrackIdB)
            .ToArray();
    }

    private static CandidatePair? CreateCandidate(
        CandidatePairKey pair,
        IReadOnlyDictionary<int, OffsetEvidence> offsets,
        int minimumHits)
    {
        var dominant = offsets
            .Where(item => item.Value.HitCount >= minimumHits)
            .OrderByDescending(item => item.Value.HitCount)
            .ThenBy(item => item.Value.MinimumHammingDistance)
            // 完全同率でも実行順序に依存しないようoffsetを最終tie-breakにする。
            .ThenBy(item => item.Key)
            .FirstOrDefault();

        return dominant.Value is null
            ? null
            : new CandidatePair(pair.TrackIdA, pair.TrackIdB, dominant.Value.MinimumHammingDistance);
    }

    private static void MatchHalf(
        FingerprintSegmentSketch current,
        ushort half,
        IReadOnlyDictionary<ushort, List<FingerprintSegmentSketch>> index,
        Dictionary<CandidatePairKey, Dictionary<int, OffsetEvidence>> evidence,
        IReadOnlySet<long>? targetTrackIds,
        int maximumDistance,
        bool skipWhenLowerHalfAlreadyMatched,
        CancellationToken cancellationToken)
    {
        foreach (var neighbor in EnumerateDistanceOneNeighborhood(half))
        {
            if (!index.TryGetValue(neighbor, out var indexedSketches))
            {
                continue;
            }

            foreach (var other in indexedSketches)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                // upper indexで見つかったPairがlower側でも距離1以内なら、lower探索ですでに同じhitを集計済み。
                // 全Segment PairのHashSetを保持せず、この局所判定で二重計上だけを除外して大規模Libraryのメモリを抑える。
                if (skipWhenLowerHalfAlreadyMatched
                    && BitOperations.PopCount(unchecked((uint)((ushort)current.Hash ^ (ushort)other.Hash))) <= 1)
                {
                    continue;
                }

                var currentIsA = current.TrackId < other.TrackId;
                var a = currentIsA ? current : other;
                var b = currentIsA ? other : current;
                var pair = CandidatePairKey.Create(a.TrackId, b.TrackId);
                var offset = a.SegmentIndex - b.SegmentIndex;
                if (!evidence.TryGetValue(pair, out var offsets))
                {
                    offsets = [];
                    evidence.Add(pair, offsets);
                }

                if (!offsets.TryGetValue(offset, out var currentEvidence))
                {
                    offsets.Add(offset, new OffsetEvidence(1, distance));
                }
                else
                {
                    offsets[offset] = new OffsetEvidence(
                        currentEvidence.HitCount + 1,
                        Math.Min(currentEvidence.MinimumHammingDistance, distance));
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

    private sealed record OffsetEvidence(int HitCount, int MinimumHammingDistance);
}

/// <summary>
/// 候補ペア探索の進捗を表す。
/// </summary>
public sealed record CandidatePairGenerationProgress(int CompletedSketches, int TotalSketches);
