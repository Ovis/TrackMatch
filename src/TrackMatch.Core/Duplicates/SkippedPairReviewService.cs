using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Duplicates;

/// <summary>
/// 旧UIから残るレビュー省略Pairの明示確定を、新しいHuman Verdictモデルへ接続する互換Service。
/// </summary>
/// <remarks>
/// レビュー省略自体は派生状態であり永続化しない。明示確定時だけ、同一GroupのKeepをPreferred Trackとして
/// 通常のConfirmedDuplicate Human Verdictへ変換する。UI移行完了後にこの互換経路は削除する。
/// </remarks>
public sealed class SkippedPairReviewService(
    ICandidateReviewMutationRepository reviewRepository,
    ITrackLookupRepository trackLookupRepository,
    IDuplicateGroupRepository groupRepository)
{
    /// <summary>
    /// 現在もレビュー省略条件を満たすPairを、Group Keepを優先TrackとするHuman Verdictとして保存する。
    /// </summary>
    public async Task ConfirmAsync(long libraryId, CandidatePairKey pair, CancellationToken cancellationToken = default)
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

        var groupA = await groupRepository.GetByTrackIdAsync(pair.TrackIdA, libraryId, cancellationToken);
        var groupB = await groupRepository.GetByTrackIdAsync(pair.TrackIdB, libraryId, cancellationToken);
        if (groupA is null || groupB is null || groupA.Id != groupB.Id || groupA.KeepStatus != DuplicateGroupKeepStatus.Selected
            || groupA.KeepTrackId is not { } keepTrackId)
        {
            throw new InvalidOperationException("現在はレビュー省略の条件を満たしていません。最新状態を再読み込みしてください。");
        }

        // Pair外のKeepとの推移関係で省略されているため、Pair自身にはPreferred Trackを選べない。
        // 新モデルでは曖昧な優劣をHuman Verdictへ保存できないので、明示確定経路は拒否する。
        if (keepTrackId != pair.TrackIdA && keepTrackId != pair.TrackIdB)
        {
            throw new InvalidOperationException("レビュー省略Pairは派生状態です。A/Bの優劣を選択して通常レビューしてください。");
        }

        var service = new DuplicateGroupService(reviewRepository, trackLookupRepository, groupRepository);
        await service.SaveReviewAsync(
            libraryId,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, keepTrackId, null),
            cancellationToken);
    }
}
