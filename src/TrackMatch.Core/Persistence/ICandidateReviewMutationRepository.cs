using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補レビューを未確定へ戻す操作を含む更新用Repository境界を定義する。
/// </summary>
public interface ICandidateReviewMutationRepository : ICandidateReviewRepository
{
    /// <summary>指定候補のレビューを削除し、未レビュー状態へ戻す。</summary>
    Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default);

    /// <summary>
    /// 操作元Library Contextを付けてGlobal Verdictを保存する。
    /// </summary>
    /// <remarks>
    /// 既存のテストFake等はLibrary Contextを必要としないため既定実装で従来Saveへ委譲する。
    /// 永続化実装はHistoryのSource Library Snapshotを残すためこのOverloadを実装する。
    /// </remarks>
    Task SaveAsync(
        CandidateReview review,
        long sourceLibraryId,
        CancellationToken cancellationToken = default)
        => SaveAsync(review, cancellationToken);

    /// <summary>
    /// 操作元Library Contextを付けてGlobal Verdictを未確定へ戻す。
    /// </summary>
    Task DeleteAsync(
        CandidatePairKey pair,
        long sourceLibraryId,
        CancellationToken cancellationToken = default)
        => DeleteAsync(pair, cancellationToken);
}
