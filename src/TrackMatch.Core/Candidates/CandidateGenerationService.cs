using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 保存済みFingerprintから候補ペアを構築し、変更Trackだけ差分更新する。
/// </summary>
public sealed class CandidateGenerationService(
    IFingerprintCatalogRepository fingerprintCatalog,
    IFingerprintSegmentSketchRepository sketchRepository,
    ICandidatePairRepository candidatePairRepository,
    ICandidateReviewRepository reviewRepository,
    FingerprintSegmentSketcher sketcher,
    CandidatePairGenerator pairGenerator)
{
    /// <summary>
    /// 保存済みFingerprintから候補ペアを生成する。
    /// </summary>
    /// <param name="fingerprintAlgorithm">対象Fingerprint Algorithm</param>
    /// <param name="options">候補生成設定</param>
    /// <param name="cancellationToken">処理のキャンセル要求</param>
    /// <param name="progress">索引更新と候補ペア探索の進捗通知先</param>
    public async Task<CandidateGenerationResult> GenerateAsync(
        int fingerprintAlgorithm,
        CandidateGenerationOptions options,
        CancellationToken cancellationToken = default,
        IProgress<CandidateGenerationProgress>? progress = null)
    {
        if (fingerprintAlgorithm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fingerprintAlgorithm));
        }

        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var fingerprintStates = await fingerprintCatalog.GetActiveStatesAsync(fingerprintAlgorithm, cancellationToken);
        var activeByTrackId = fingerprintStates.ToDictionary(item => item.TrackId);
        var cachedStates = await sketchRepository.GetTrackStatesAsync(fingerprintAlgorithm, options, cancellationToken);

        var changedTrackIds = fingerprintStates
            .Where(item => !cachedStates.TryGetValue(item.TrackId, out var cachedAt)
                || cachedAt != item.ExtractedAtUtc)
            .Select(item => item.TrackId)
            .ToHashSet();
        var inactiveTrackIds = cachedStates.Keys
            .Where(trackId => !activeByTrackId.ContainsKey(trackId))
            .ToHashSet();

        // キャッシュがない初回や候補生成パラメータ変更時は全Trackを索引対象にし、候補集合も全再構築する。
        var fullRebuild = cachedStates.Count == 0;
        if (fullRebuild)
        {
            changedTrackIds.UnionWith(activeByTrackId.Keys);
        }

        if (changedTrackIds.Count != 0)
        {
            var changedFingerprints = changedTrackIds.Count == fingerprintStates.Count
                ? await fingerprintCatalog.GetActiveAsync(fingerprintAlgorithm, cancellationToken)
                : await fingerprintCatalog.GetActiveByTrackIdsAsync(fingerprintAlgorithm, changedTrackIds, cancellationToken);

            var completedTracks = 0;
            progress?.Report(new CandidateGenerationProgress(
                CandidateGenerationProgressPhase.UpdatingIndex,
                completedTracks,
                changedFingerprints.Count));

            foreach (var fingerprint in changedFingerprints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sketches = sketcher.Create(fingerprint, options);
                await sketchRepository.ReplaceTrackAsync(fingerprint, options, sketches, cancellationToken);
                completedTracks++;
                progress?.Report(new CandidateGenerationProgress(
                    CandidateGenerationProgressPhase.UpdatingIndex,
                    completedTracks,
                    changedFingerprints.Count));
            }
        }

        await sketchRepository.PruneAsync(fingerprintAlgorithm, options, cancellationToken);
        var allSketches = await sketchRepository.GetAllAsync(fingerprintAlgorithm, options, cancellationToken);

        var pairProgress = new Progress<CandidatePairGenerationProgress>(value =>
            progress?.Report(new CandidateGenerationProgress(
                CandidateGenerationProgressPhase.SearchingPairs,
                value.CompletedSketches,
                value.TotalSketches)));

        IReadOnlyList<CandidatePair> generatedPairs;
        if (fullRebuild)
        {
            generatedPairs = pairGenerator.GenerateFromSketches(
                allSketches,
                targetTrackIds: null,
                options.MaximumSegmentHashHammingDistance,
                pairProgress);
        }
        else
        {
            var affectedTrackIds = changedTrackIds
                .Concat(inactiveTrackIds)
                .ToHashSet();
            if (affectedTrackIds.Count == 0)
            {
                progress?.Report(new CandidateGenerationProgress(
                    CandidateGenerationProgressPhase.SearchingPairs,
                    allSketches.Count,
                    allSketches.Count));
                generatedPairs = [];
            }
            else
            {
                generatedPairs = pairGenerator.GenerateFromSketches(
                    allSketches,
                    affectedTrackIds,
                    options.MaximumSegmentHashHammingDistance,
                    pairProgress);
            }
        }

        var excluded = await reviewRepository.GetExcludedPairKeysAsync(cancellationToken);
        var pairs = generatedPairs
            .Where(pair => !excluded.Contains(CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB)))
            .ToArray();

        if (fullRebuild)
        {
            await candidatePairRepository.ReplaceAllAsync(pairs, cancellationToken);
        }
        else
        {
            var affectedTrackIds = changedTrackIds
                .Concat(inactiveTrackIds)
                .ToArray();
            await candidatePairRepository.ReplaceForTracksAsync(affectedTrackIds, pairs, cancellationToken);

            // レビューはFingerprint更新と独立して発生するため、無変更実行でも新規レビュー済みペアを候補集合から除外する。
            await candidatePairRepository.DeleteAsync(excluded.ToArray(), cancellationToken);
        }

        return new CandidateGenerationResult(
            fingerprintStates.Count,
            allSketches.Count,
            pairs,
            changedTrackIds.Count,
            fullRebuild);
    }
}

/// <summary>
/// 候補生成処理の進捗段階を表す。
/// </summary>
public enum CandidateGenerationProgressPhase
{
    UpdatingIndex,
    SearchingPairs,
}

/// <summary>
/// 候補生成処理の件数進捗を表す。
/// </summary>
public sealed record CandidateGenerationProgress(
    CandidateGenerationProgressPhase Phase,
    int CompletedCount,
    int TotalCount);
