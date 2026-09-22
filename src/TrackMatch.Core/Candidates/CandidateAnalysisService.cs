using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 保存済み候補ペアをraw Fingerprintで詳細比較し、測定値を永続化する。
/// </summary>
public sealed class CandidateAnalysisService(
    IFingerprintCatalogRepository fingerprintCatalog,
    ICandidatePairRepository candidatePairRepository,
    ICandidateComparisonRepository comparisonRepository,
    FingerprintComparer comparer,
    ILogger<CandidateAnalysisService>? logger = null)
{
    // 数万～十数万件の比較を最後までMemoryに保持すると、Cancel時に全計算結果を失う。
    // SQLiteへのTransaction回数を抑えつつ再開可能性を確保するため、一定件数ごとにCheckpointする。
    private const int ComparisonCheckpointSize = 500;
    private readonly ILogger<CandidateAnalysisService> _logger = logger ?? NullLogger<CandidateAnalysisService>.Instance;
    /// <summary>
    /// 保存済み候補ペアを詳細比較する。
    /// </summary>
    /// <param name="fingerprintAlgorithm">対象Fingerprint Algorithm</param>
    /// <param name="cancellationToken">処理のキャンセル要求</param>
    /// <param name="progress">候補ペア単位の比較進捗通知先</param>
    public async Task<CandidateAnalysisResult> AnalyzeAsync(
        int fingerprintAlgorithm,
        CancellationToken cancellationToken = default,
        IProgress<CandidateAnalysisProgress>? progress = null)
    {
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        var pairs = await candidatePairRepository.GetAllAsync(cancellationToken);
        var fingerprints = await fingerprintCatalog.GetActiveAsync(fingerprintAlgorithm, cancellationToken);
        var fingerprintsByTrackId = fingerprints.ToDictionary(item => item.TrackId);
        var comparedAtByPair = await comparisonRepository.GetComparedAtUtcAsync(cancellationToken);
        var changedComparisons = new List<CandidateComparison>();
        var checkpointStopwatch = Stopwatch.StartNew();
        var checkpointComparisonElapsed = TimeSpan.Zero;
        var checkpointCompared = 0;
        var checkpointMaximumComparisonElapsed = TimeSpan.Zero;
        var reused = 0;
        var skipped = 0;
        var completed = 0;
        progress?.Report(new CandidateAnalysisProgress(completed, pairs.Count));

        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fingerprintsByTrackId.TryGetValue(pair.TrackIdA, out var a) ||
                !fingerprintsByTrackId.TryGetValue(pair.TrackIdB, out var b))
            {
                // Track更新後などで候補生成時のFingerprintが失効している場合は、古い組み合わせを比較しない。
                skipped++;
                completed++;
                progress?.Report(new CandidateAnalysisProgress(completed, pairs.Count));
                continue;
            }

            var pairKey = CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB);
            var newestFingerprintAt = a.ExtractedAtUtc >= b.ExtractedAtUtc ? a.ExtractedAtUtc : b.ExtractedAtUtc;
            if (comparedAtByPair.TryGetValue(pairKey, out var comparedAtUtc)
                && comparedAtUtc >= newestFingerprintAt)
            {
                // 両方のFingerprintが前回比較時点から変わっていなければ、raw Fingerprint比較は再実行しない。
                reused++;
                completed++;
                progress?.Report(new CandidateAnalysisProgress(completed, pairs.Count));
                continue;
            }

            var comparisonStopwatch = Stopwatch.StartNew();
            var result = comparer.Compare(a.Fingerprint, b.Fingerprint);
            comparisonStopwatch.Stop();
            checkpointComparisonElapsed += comparisonStopwatch.Elapsed;
            checkpointCompared++;
            if (comparisonStopwatch.Elapsed > checkpointMaximumComparisonElapsed)
            {
                checkpointMaximumComparisonElapsed = comparisonStopwatch.Elapsed;
            }
            changedComparisons.Add(new CandidateComparison(
                pair.TrackIdA,
                pair.TrackIdB,
                result.Similarity,
                result.BestOffsetItems,
                result.BestOffset,
                result.MatchedItems,
                result.MatchedDuration,
                result.CoverageA,
                result.CoverageB,
                CalculateDurationRatio(a.Fingerprint.Duration, b.Fingerprint.Duration)));
            completed++;
            progress?.Report(new CandidateAnalysisProgress(completed, pairs.Count));

            if (changedComparisons.Count >= ComparisonCheckpointSize)
            {
                var persistenceStopwatch = Stopwatch.StartNew();
                await comparisonRepository.UpsertAsync(changedComparisons, cancellationToken);
                persistenceStopwatch.Stop();
                checkpointStopwatch.Stop();

                _logger.LogInformation(
                    "Candidate detail comparison checkpoint. Completed={Completed}/{Total}, Compared={Compared}, " +
                    "ComparisonElapsedMs={ComparisonElapsedMs:F1}, AverageComparisonMs={AverageComparisonMs:F2}, " +
                    "MaximumComparisonMs={MaximumComparisonMs:F1}, PersistenceElapsedMs={PersistenceElapsedMs:F1}, " +
                    "CheckpointElapsedMs={CheckpointElapsedMs:F1}",
                    completed,
                    pairs.Count,
                    checkpointCompared,
                    checkpointComparisonElapsed.TotalMilliseconds,
                    checkpointCompared == 0 ? 0d : checkpointComparisonElapsed.TotalMilliseconds / checkpointCompared,
                    checkpointMaximumComparisonElapsed.TotalMilliseconds,
                    persistenceStopwatch.Elapsed.TotalMilliseconds,
                    checkpointStopwatch.Elapsed.TotalMilliseconds);

                changedComparisons.Clear();
                checkpointStopwatch.Restart();
                checkpointComparisonElapsed = TimeSpan.Zero;
                checkpointCompared = 0;
                checkpointMaximumComparisonElapsed = TimeSpan.Zero;
            }
        }

        var analyzed = completed - reused - skipped;
        if (changedComparisons.Count != 0)
        {
            var persistenceStopwatch = Stopwatch.StartNew();
            await comparisonRepository.UpsertAsync(changedComparisons, cancellationToken);
            persistenceStopwatch.Stop();
            checkpointStopwatch.Stop();
            _logger.LogInformation(
                "Candidate detail comparison final checkpoint. Completed={Completed}/{Total}, Compared={Compared}, " +
                "ComparisonElapsedMs={ComparisonElapsedMs:F1}, AverageComparisonMs={AverageComparisonMs:F2}, " +
                "MaximumComparisonMs={MaximumComparisonMs:F1}, PersistenceElapsedMs={PersistenceElapsedMs:F1}, " +
                "CheckpointElapsedMs={CheckpointElapsedMs:F1}",
                completed,
                pairs.Count,
                checkpointCompared,
                checkpointComparisonElapsed.TotalMilliseconds,
                checkpointCompared == 0 ? 0d : checkpointComparisonElapsed.TotalMilliseconds / checkpointCompared,
                checkpointMaximumComparisonElapsed.TotalMilliseconds,
                persistenceStopwatch.Elapsed.TotalMilliseconds,
                checkpointStopwatch.Elapsed.TotalMilliseconds);
        }

        return new CandidateAnalysisResult(pairs.Count, analyzed, reused, skipped);
    }

    private static double CalculateDurationRatio(TimeSpan a, TimeSpan b)
    {
        var maximum = Math.Max(a.TotalSeconds, b.TotalSeconds);
        return maximum <= 0d ? 0d : Math.Min(a.TotalSeconds, b.TotalSeconds) / maximum;
    }
}

/// <summary>
/// 候補ペアの詳細比較進捗を表す。
/// </summary>
public sealed record CandidateAnalysisProgress(int CompletedPairs, int TotalPairs);
