using System.Numerics;
using TrackMatch.Core.Fingerprinting;

namespace TrackMatch.Core.Comparison;

/// <summary>
/// Chromaprintの32bit raw fingerprintをOffset探索しながら比較する。
/// </summary>
/// <remarks>
/// Similarityは各32bit値のXORに対するHamming距離を固定32bit幅で正規化する。
/// Probeでは短い側の80%以上が重なるOffsetだけを評価し、短い偶然一致が最良値になることを避ける。
/// </remarks>
public sealed class FingerprintComparer
{
    // Chromaprint Algorithm 2の既定構成は11025 Hz、1 fingerprint itemあたり1365 samplesである。
    // fpcalc側でも-algorithm 2を明示し、この値と生成条件を固定する。
    public static readonly TimeSpan ItemDuration = TimeSpan.FromSeconds(1365d / 11025d);

    private const double MinimumOverlapRatio = 0.8;

    public FingerprintComparisonResult Compare(AudioFingerprint a, AudioFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Values.Count == 0 || b.Values.Count == 0)
        {
            throw new ArgumentException("Fingerprintが空である。");
        }

        var shorterLength = Math.Min(a.Values.Count, b.Values.Count);
        var minimumOverlap = Math.Max(1, (int)Math.Ceiling(shorterLength * MinimumOverlapRatio));

        var minimumOffset = -(b.Values.Count - minimumOverlap);
        var maximumOffset = a.Values.Count - minimumOverlap;

        double bestSimilarity = double.NegativeInfinity;
        var bestOffset = 0;
        var bestOverlap = 0;

        for (var offset = minimumOffset; offset <= maximumOffset; offset++)
        {
            var aStart = Math.Max(0, offset);
            var bStart = Math.Max(0, -offset);
            var overlap = Math.Min(a.Values.Count - aStart, b.Values.Count - bStart);

            if (overlap < minimumOverlap)
            {
                continue;
            }

            long differingBits = 0;
            for (var i = 0; i < overlap; i++)
            {
                differingBits += BitOperations.PopCount(a.Values[aStart + i] ^ b.Values[bStart + i]);
            }

            var similarity = 1d - (double)differingBits / (overlap * 32d);
            if (similarity > bestSimilarity ||
                (Math.Abs(similarity - bestSimilarity) < 1e-12 && overlap > bestOverlap))
            {
                bestSimilarity = similarity;
                bestOffset = offset;
                bestOverlap = overlap;
            }
        }

        return new FingerprintComparisonResult(
            bestSimilarity,
            bestOffset,
            Scale(ItemDuration, bestOffset),
            bestOverlap,
            Scale(ItemDuration, bestOverlap),
            (double)bestOverlap / a.Values.Count,
            (double)bestOverlap / b.Values.Count);
    }

    private static TimeSpan Scale(TimeSpan duration, int multiplier)
    {
        return TimeSpan.FromTicks(duration.Ticks * multiplier);
    }
}
