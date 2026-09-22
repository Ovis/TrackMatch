using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Candidates;

/// <summary>
/// 保存済みFingerprintから候補ペアを構築し、変更TrackとLibraryTrack Pendingだけ差分更新する。
/// </summary>
public sealed class CandidateGenerationService(
    IFingerprintCatalogRepository fingerprintCatalog,
    IFingerprintSegmentSketchRepository sketchRepository,
    ICandidatePairRepository candidatePairRepository,
    ICandidateReviewRepository reviewRepository,
    FingerprintSegmentSketcher sketcher,
    CandidatePairGenerator pairGenerator,
    ICandidateGenerationWorkRepository? workRepository = null,
    ICandidateGenerationStateRepository? stateRepository = null,
    ICandidateGenerationCommitRepository? commitRepository = null)
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
        var pendingTrackIds = workRepository is null
            ? new HashSet<long>()
            : (await workRepository.GetPendingTrackIdsAsync(cancellationToken)).ToHashSet();
        var desiredState = CandidateGenerationState.Create(fingerprintAlgorithm, options);
        var completedState = stateRepository is null
            ? desiredState
            : await stateRepository.GetAsync(cancellationToken);

        var changedTrackIds = fingerprintStates
            .Where(item => !cachedStates.TryGetValue(item.TrackId, out var cachedAt)
                || cachedAt != item.ExtractedAtUtc)
            .Select(item => item.TrackId)
            .ToHashSet();

        // Sketch生成条件とCandidate判定条件を分離し、Candidate条件だけの変更では高価なSketch再生成を避ける。
        var sketchConfigurationChanged = completedState is null
            || completedState.FingerprintAlgorithm != fingerprintAlgorithm
            || completedState.SegmentLengthItems != options.SegmentLengthItems
            || completedState.SegmentStrideItems != options.SegmentStrideItems;
        var sketchFullRebuild = cachedStates.Count == 0 || sketchConfigurationChanged;
        var fullRebuild = completedState != desiredState || sketchFullRebuild;
        if (sketchFullRebuild)
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

        // Missing TrackはActive Fingerprint集合から外れるが、それだけを理由にMachine Comparison Cacheまで失効させない。
        // Content ChangeとForce Reanalysisは各専用経路でCandidatePairsを明示的に無効化するため、
        // Candidate Generationでは「現在再評価が必要なTrack」だけを差分置換対象にする。
        var affectedTrackIds = changedTrackIds
            .Concat(pendingTrackIds)
            .ToHashSet();
        IReadOnlyList<CandidatePair> generatedPairs;
        if (fullRebuild)
        {
            generatedPairs = pairGenerator.GenerateFromSketches(
                allSketches,
                targetTrackIds: null,
                options,
                pairProgress);
        }
        else if (affectedTrackIds.Count == 0)
        {
            progress?.Report(new CandidateGenerationProgress(
                CandidateGenerationProgressPhase.SearchingPairs,
                allSketches.Count,
                allSketches.Count));
            generatedPairs = [];
        }
        else
        {
            // PendingはFingerprint/Sketchが既知でも、新しいLibrary MembershipやGeneration Version変更によって
            // このLibrary内の組合せ探索だけが未完了なTrackを表すため、changed Trackと同じ起点集合へ含める。
            generatedPairs = pairGenerator.GenerateFromSketches(
                allSketches,
                affectedTrackIds,
                options,
                pairProgress);
        }

        var reviewedPairs = await reviewRepository.GetExcludedPairKeysAsync(cancellationToken);
        var reviewablePairs = generatedPairs
            .Where(pair => !reviewedPairs.Contains(CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB)))
            .ToArray();

        var completedPending = pendingTrackIds
            .Where(activeByTrackId.ContainsKey)
            .Where(affectedTrackIds.Contains)
            .ToArray();

        if (commitRepository is not null)
        {
            // Production SQLiteではCandidate置換、Pending解除、完了マーカー更新を同一Transactionで確定する。
            // 途中失敗で「新StateだがCandidateは旧状態」のような部分Commitを残さないための境界である。
            await commitRepository.CommitAsync(
                fullRebuild,
                affectedTrackIds.ToArray(),
                generatedPairs,
                completedPending,
                desiredState,
                cancellationToken);
        }
        else
        {
            // 単体テスト等の汎用Repository構成では従来境界を利用する。
            if (fullRebuild)
            {
                await candidatePairRepository.ReplaceAllAsync(generatedPairs, cancellationToken);
            }
            else
            {
                await candidatePairRepository.ReplaceForTracksAsync(affectedTrackIds.ToArray(), generatedPairs, cancellationToken);
            }

            if (workRepository is not null && completedPending.Length != 0)
            {
                await workRepository.MarkCompletedAsync(completedPending, cancellationToken);
            }

            if (stateRepository is not null && (fullRebuild || completedState != desiredState))
            {
                await stateRepository.SaveAsync(desiredState, cancellationToken);
            }
        }

        return new CandidateGenerationResult(
            fingerprintStates.Count,
            allSketches.Count,
            reviewablePairs,
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
