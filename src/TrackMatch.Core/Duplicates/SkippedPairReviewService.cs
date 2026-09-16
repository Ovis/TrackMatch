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

        // UI表示後にKeepやGroupが変わる可能性があるため、表示時のIsReviewSkippedを信用せず保存直前に再検証する。
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

        var review = new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, null);
        var proposedReviews = currentReviews.Append(review).ToArray();
        var existingGroups = await groupRepository.GetAllGlobalAsync(cancellationToken);

        // ConfirmedDuplicateの推移関係とNotDuplicateの矛盾をCommit前に検証する。
        // レビュー省略は新しいVerdict種別ではなく、明示確定後は通常のGlobal edgeとして扱う。
        _ = DuplicateGroupPlanner.Build(proposedReviews, existingGroups);

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
}
