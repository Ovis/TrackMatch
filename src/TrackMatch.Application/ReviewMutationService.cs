using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// Human Verdict変更と、そのAffected Closureに限定した派生状態更新を統括する。
/// </summary>
public sealed class ReviewMutationService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository,
    IProjectionStateRepository projectionStateRepository,
    DuplicateGroupService duplicateGroupService,
    SupplementalCandidateReconciliationService supplementalReconciliationService,
    GlobalMutationGate? mutationGate = null,
    ILogger<ReviewMutationService>? logger = null)
{
    private readonly GlobalMutationGate _mutationGate = mutationGate ?? GlobalMutationGate.Shared;
    private readonly ILogger<ReviewMutationService> _logger = logger ?? NullLogger<ReviewMutationService>.Instance;

    /// <summary>
    /// Human Verdictを保存し、レビュー対象のBefore/After ClosureだけをReview可能な状態へ収束させる。
    /// </summary>
    public async Task<ReviewMutationResult> SaveAsync(
        long libraryId,
        CandidateReview review,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterReviewGateAsync(cancellationToken);
        await EnsureProjectionIsCleanAsync(libraryId, cancellationToken);
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        await ValidatePairAsync(libraryId, review.Pair, requireActiveTracks: true, cancellationToken);

        var currentReviews = await reviewRepository.GetAllAsync(cancellationToken);
        var current = currentReviews.SingleOrDefault(item => item.Pair == review.Pair);
        if (current == review)
        {
            return CreateLocalResult(review.Pair);
        }

        if (current is not null
            && current.Decision == review.Decision
            && current.PreferredTrackId == review.PreferredTrackId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
            return CreateLocalResult(review.Pair);
        }

        return await MutateAsync(
            libraryId,
            review.Pair,
            token => duplicateGroupService.SaveReviewAsync(libraryId, review, token),
            async () => (await reviewRepository.GetAllAsync(CancellationToken.None))
                .SingleOrDefault(item => item.Pair == review.Pair) == review,
            cancellationToken);
    }

    /// <summary>
    /// Current Human Verdictを削除し、残ったVerdictからAffected Closureを収束させる。
    /// </summary>
    public async Task<ReviewMutationResult> DeleteAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken = default)
    {
        using var gate = await EnterReviewGateAsync(cancellationToken);
        await EnsureProjectionIsCleanAsync(libraryId, cancellationToken);
        await ValidatePairAsync(libraryId, pair, requireActiveTracks: false, cancellationToken);
        var currentReviews = await reviewRepository.GetAllAsync(cancellationToken);
        if (currentReviews.All(item => item.Pair != pair))
        {
            return CreateLocalResult(pair);
        }

        return await MutateAsync(
            libraryId,
            pair,
            token => duplicateGroupService.DeleteReviewAsync(libraryId, pair, token),
            async () => (await reviewRepository.GetAllAsync(CancellationToken.None))
                .All(item => item.Pair != pair),
            cancellationToken);
    }

    private async Task<ReviewMutationResult> MutateAsync(
        long libraryId,
        CandidatePairKey pair,
        Func<CancellationToken, Task> mutateCanonicalAsync,
        Func<Task<bool>> isCanonicalAppliedAsync,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var beforeGroups = await groupRepository.GetByLibraryIdAsync(libraryId, cancellationToken);
        var cleanupTrackIds = GetRelatedTrackIds(beforeGroups, pair);

        cancellationToken.ThrowIfCancellationRequested();
        await projectionStateRepository.MarkReviewMutationStartedAsync(libraryId, cancellationToken);
        var groupKeepStopwatch = Stopwatch.StartNew();
        try
        {
            // Dirty化以降はCanonical/Derivedを外部Cancellationで中断せず、整合状態まで完走させる。
            await mutateCanonicalAsync(CancellationToken.None);
            groupKeepStopwatch.Stop();
            return await CompleteDerivedStateAsync(
                libraryId,
                pair,
                cleanupTrackIds,
                totalStopwatch,
                groupKeepStopwatch.Elapsed,
                recoveryPerformed: false);
        }
        catch (Exception initialFailure)
        {
            groupKeepStopwatch.Stop();
            if (!await IsCanonicalAppliedConservativelyAsync(isCanonicalAppliedAsync))
            {
                await TryClearDirtyAfterUncommittedFailureAsync(libraryId);
                ExceptionDispatchInfo.Capture(initialFailure).Throw();
            }

            _logger.LogWarning(
                initialFailure,
                "Verdict Commit後の派生更新に失敗したためImmediate Recoveryを開始する LibraryId={LibraryId} Pair={TrackIdA}-{TrackIdB}",
                libraryId,
                pair.TrackIdA,
                pair.TrackIdB);
            try
            {
                await duplicateGroupService.SynchronizeGlobalForLibraryAsync(libraryId, CancellationToken.None);
                return await CompleteDerivedStateAsync(
                    libraryId,
                    pair,
                    cleanupTrackIds,
                    totalStopwatch,
                    groupKeepStopwatch.Elapsed,
                    recoveryPerformed: true);
            }
            catch (Exception recoveryFailure)
            {
                throw new ReviewMutationRecoveryException(
                    "Human Verdictは保存されましたが、派生状態のImmediate Recoveryに失敗しました。新しいレビューを停止して再起動してください。",
                    initialFailure,
                    recoveryFailure);
            }
        }
    }

    private async Task<ReviewMutationResult> CompleteDerivedStateAsync(
        long libraryId,
        CandidatePairKey pair,
        IReadOnlySet<long> cleanupTrackIds,
        Stopwatch totalStopwatch,
        TimeSpan groupKeepElapsed,
        bool recoveryPerformed)
    {
        var afterGroups = await groupRepository.GetByLibraryIdAsync(libraryId, CancellationToken.None);
        var affectedGroups = afterGroups
            .Where(group => group.GlobalTrackIds.Any(cleanupTrackIds.Contains))
            .ToArray();
        var affectedTrackIds = cleanupTrackIds.ToHashSet();
        foreach (var group in affectedGroups)
        {
            affectedTrackIds.UnionWith(group.GlobalTrackIds);
        }

        var supplemental = await supplementalReconciliationService.ReconcileAsync(
            affectedGroups,
            cleanupTrackIds,
            CancellationToken.None);
        var affectedPairKeys = supplemental.AffectedPairKeys.ToHashSet();
        affectedPairKeys.Add(pair);

        await MarkAffectedOtherLibrariesDirtyAsync(libraryId, affectedTrackIds);
        await projectionStateRepository.SetLibraryKeepProjectionDirtyAsync(
            libraryId,
            isDirty: false,
            CancellationToken.None);
        // Global Cleanは最後にする。途中失敗時は次回起動がGlobal Recoveryへ安全側に倒れる。
        await projectionStateRepository.SetGlobalGroupsDirtyAsync(
            isDirty: false,
            CancellationToken.None);
        totalStopwatch.Stop();

        _logger.LogInformation(
            "Review Mutation完了 LibraryId={LibraryId} Pair={TrackIdA}-{TrackIdB} TotalMs={TotalMs:F1} GroupKeepMs={GroupKeepMs:F1} AffectedTracks={AffectedTrackCount} AffectedPairs={AffectedPairCount} RecoveryPerformed={RecoveryPerformed}",
            libraryId,
            pair.TrackIdA,
            pair.TrackIdB,
            totalStopwatch.Elapsed.TotalMilliseconds,
            groupKeepElapsed.TotalMilliseconds,
            affectedTrackIds.Count,
            affectedPairKeys.Count,
            recoveryPerformed);
        return new ReviewMutationResult(affectedTrackIds, affectedPairKeys, recoveryPerformed);
    }

    private async Task MarkAffectedOtherLibrariesDirtyAsync(
        long currentLibraryId,
        IReadOnlyCollection<long> affectedTrackIds)
    {
        var tracksByLibrary = new Dictionary<long, HashSet<long>>();
        foreach (var trackId in affectedTrackIds)
        {
            foreach (var libraryId in await trackLookupRepository.GetLibraryIdsAsync(
                         trackId,
                         CancellationToken.None))
            {
                if (libraryId == currentLibraryId)
                {
                    continue;
                }

                if (!tracksByLibrary.TryGetValue(libraryId, out var trackIds))
                {
                    tracksByLibrary[libraryId] = trackIds = [];
                }

                trackIds.Add(trackId);
            }
        }

        foreach (var libraryId in tracksByLibrary
                     .Where(item => item.Value.Count >= 2)
                     .Select(item => item.Key))
        {
            await projectionStateRepository.SetLibraryKeepProjectionDirtyAsync(
                libraryId,
                isDirty: true,
                CancellationToken.None);
        }
    }

    private async Task<IDisposable> EnterReviewGateAsync(CancellationToken cancellationToken)
        => await _mutationGate.TryEnterAsync(cancellationToken)
            ?? throw new InvalidOperationException("別の更新処理が実行中です。完了後にレビューを再実行してください。");

    private async Task EnsureProjectionIsCleanAsync(
        long libraryId,
        CancellationToken cancellationToken)
    {
        if (await projectionStateRepository.IsGlobalGroupsDirtyAsync(cancellationToken)
            || await projectionStateRepository.IsLibraryKeepProjectionDirtyAsync(libraryId, cancellationToken))
        {
            throw new InvalidOperationException("派生状態のRecoveryが必要なため、新しいレビューを開始できません。Libraryを再読み込みしてください。");
        }
    }

    private static async Task<bool> IsCanonicalAppliedConservativelyAsync(
        Func<Task<bool>> isCanonicalAppliedAsync)
    {
        try
        {
            return await isCanonicalAppliedAsync();
        }
        catch
        {
            // Commit成否を確認できない場合は、誤ってCleanへ戻すよりRecoveryを試みてDirtyを維持する。
            return true;
        }
    }

    private async Task TryClearDirtyAfterUncommittedFailureAsync(long libraryId)
    {
        try
        {
            await projectionStateRepository.SetLibraryKeepProjectionDirtyAsync(
                libraryId,
                isDirty: false,
                CancellationToken.None);
            await projectionStateRepository.SetGlobalGroupsDirtyAsync(
                isDirty: false,
                CancellationToken.None);
        }
        catch
        {
            // Clean復帰に失敗した場合はDirtyを残し、Cross-restart Recoveryへ委ねる。
        }
    }

    private async Task ValidatePairAsync(
        long libraryId,
        CandidatePairKey pair,
        bool requireActiveTracks,
        CancellationToken cancellationToken)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        var a = await trackLookupRepository.GetByIdAsync(pair.TrackIdA, cancellationToken);
        var b = await trackLookupRepository.GetByIdAsync(pair.TrackIdB, cancellationToken);
        if (a is null || b is null)
        {
            throw new InvalidOperationException("レビュー対象のTrackが見つかりません。");
        }

        if (requireActiveTracks && (a.IsMissing || b.IsMissing))
        {
            throw new InvalidOperationException("Missing状態のTrackへ新しいHuman Verdictを保存できません。");
        }

        if (requireActiveTracks
            && (!await trackLookupRepository.IsHumanVerdictUsableAsync(pair.TrackIdA, cancellationToken)
                || !await trackLookupRepository.IsHumanVerdictUsableAsync(pair.TrackIdB, cancellationToken)))
        {
            throw new InvalidOperationException("Content Verificationまたは再評価中のTrackへHuman Verdictを保存できません。");
        }

        if (!await trackLookupRepository.IsInLibraryAsync(pair.TrackIdA, libraryId, cancellationToken)
            || !await trackLookupRepository.IsInLibraryAsync(pair.TrackIdB, libraryId, cancellationToken))
        {
            throw new InvalidOperationException("現在LibraryのMembership外Track同士を通常レビューとして扱うことはできません。");
        }
    }

    private static HashSet<long> GetRelatedTrackIds(
        IReadOnlyCollection<DuplicateGroup> groups,
        CandidatePairKey pair)
    {
        var result = new HashSet<long> { pair.TrackIdA, pair.TrackIdB };
        foreach (var group in groups.Where(group =>
                     group.GlobalTrackIds.Contains(pair.TrackIdA)
                     || group.GlobalTrackIds.Contains(pair.TrackIdB)))
        {
            result.UnionWith(group.GlobalTrackIds);
        }

        return result;
    }

    private static ReviewMutationResult CreateLocalResult(CandidatePairKey pair)
        => new(
            new HashSet<long> { pair.TrackIdA, pair.TrackIdB },
            new HashSet<CandidatePairKey> { pair },
            RecoveryPerformed: false);
}

/// <summary>
/// Verdict Commit後の通常Derived更新とImmediate Recoveryがともに失敗したことを表す。
/// </summary>
public sealed class ReviewMutationRecoveryException : Exception
{
    /// <summary>二段階の失敗を保持する例外を生成する。</summary>
    public ReviewMutationRecoveryException(
        string message,
        Exception initialFailure,
        Exception recoveryFailure)
        : base(message, new AggregateException(initialFailure, recoveryFailure))
    {
        InitialFailure = initialFailure;
        RecoveryFailure = recoveryFailure;
    }

    /// <summary>Verdict Commit後に最初に発生したDerived更新失敗。</summary>
    public Exception InitialFailure { get; }

    /// <summary>1回だけ実行したImmediate Recoveryの失敗。</summary>
    public Exception RecoveryFailure { get; }
}

/// <summary>
/// Review Mutation後にCandidate Presentationを再評価する範囲を表す。
/// </summary>
public sealed record ReviewMutationResult(
    IReadOnlySet<long> AffectedTrackIds,
    IReadOnlySet<CandidatePairKey> AffectedPairKeys,
    bool RecoveryPerformed);
