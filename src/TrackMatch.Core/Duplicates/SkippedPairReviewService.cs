using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Libraryでレビュー省略中の非Keep Track同士を、Keepを変更せず明示的なConfirmedDuplicateとして確定する。
/// </summary>
public sealed class SkippedPairReviewService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository)
{
    /// <summary>
    /// 現在もレビュー省略条件を満たすPairだけをConfirmedDuplicateとして保存する。
    /// </summary>
    /// <remarks>
    /// UI表示時の派生状態は信用せず、論理矛盾検証の前後で最新のTrack、Membership、Human Verdict、Group、Keepを再確認する。
    /// TrackMatchの通常UI操作は直列化されているため、このServiceでは複数Repositoryを跨ぐ長時間のSQLite Transactionは保持しない。
    /// </remarks>
    /// <param name="libraryId">操作元Library ID</param>
    /// <param name="pair">明示確定するGlobal Track Pair</param>
    /// <param name="cancellationToken">Human Verdict Commit前までのキャンセル要求</param>
    public async Task ConfirmAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken = default)
    {
        if (libraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(libraryId));
        }

        var currentReviews = await EnsureStillSkippedAsync(libraryId, pair, cancellationToken);
        var review = new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, null);
        var proposedReviews = currentReviews.Append(review).ToArray();
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);

        // ConfirmedDuplicateの推移関係とNotDuplicateの矛盾をCommit前に検証する。
        // レビュー省略は新しいVerdict種別ではなく、明示確定後は通常のGlobal edgeとして扱う。
        _ = DuplicateGroupPlanner.Build(proposedReviews, existingGroups);

        // Planner検証中に詳細画面等からKeepやVerdictが変わった場合も、古い省略状態のまま保存しない。
        // SQLiteを跨ぐ外部プロセス更新までCAS保証するものではないが、通常UI操作で生じるstale stateはCommit直前に拒否する。
        await EnsureStillSkippedAsync(libraryId, pair, cancellationToken);

        await reviewRepository.SaveAsync(review, libraryId, cancellationToken);
        var groupService = new DuplicateGroupService(reviewRepository, trackLookupRepository, groupRepository);
        try
        {
            // Verdict Commit後はCurrent Verdictが正本なので、呼び出し元Cancelで派生Group更新だけを中断しない。
            await groupService.SynchronizeGlobalAsync(CancellationToken.None);
        }
        catch
        {
            try
            {
                await groupService.SynchronizeGlobalAsync(CancellationToken.None);
            }
            catch
            {
                // 最初の同期失敗を呼び出し元へ返すため、修復試行の例外では上書きしない。
            }

            throw;
        }
    }

    /// <summary>
    /// Pairが現在もレビュー省略条件を満たし、明示確定可能であることを検証する。
    /// </summary>
    /// <returns>検証時点のCurrent Human Verdict一覧</returns>
    private async Task<IReadOnlyList<CandidateReview>> EnsureStillSkippedAsync(
        long libraryId,
        CandidatePairKey pair,
        CancellationToken cancellationToken)
    {
        var trackA = await trackLookupRepository.GetByIdAsync(pair.TrackIdA, cancellationToken);
        var trackB = await trackLookupRepository.GetByIdAsync(pair.TrackIdB, cancellationToken);
        if (trackA is null || trackB is null || trackA.IsMissing || trackB.IsMissing)
        {
            throw new InvalidOperationException("レビュー対象のTrackが利用可能ではありません。最新状態を再読み込みしてください。");
        }

        if (!await trackLookupRepository.IsInLibraryAsync(pair.TrackIdA, libraryId, cancellationToken)
            || !await trackLookupRepository.IsInLibraryAsync(pair.TrackIdB, libraryId, cancellationToken))
        {
            throw new InvalidOperationException("現在LibraryのMembership外Trackをレビュー省略Pairとして確定できません。");
        }

        var currentReviews = await reviewRepository.GetAllAsync(cancellationToken);
        if (currentReviews.Any(review => review.Pair == pair))
        {
            throw new InvalidOperationException("このPairにはすでにHuman Verdictがあります。最新状態を再読み込みしてください。");
        }

        var groupA = await groupRepository.GetByTrackIdAsync(pair.TrackIdA, libraryId, cancellationToken);
        var groupB = await groupRepository.GetByTrackIdAsync(pair.TrackIdB, libraryId, cancellationToken);
        if (groupA is null
            || groupB is null
            || groupA.Id != groupB.Id
            || groupA.KeepStatus != DuplicateGroupKeepStatus.Selected
            || groupA.KeepTrackId is not { } keepTrackId
            || keepTrackId == pair.TrackIdA
            || keepTrackId == pair.TrackIdB)
        {
            throw new InvalidOperationException("現在はレビュー省略の条件を満たしていません。最新状態を再読み込みしてください。");
        }

        return currentReviews;
    }
}
