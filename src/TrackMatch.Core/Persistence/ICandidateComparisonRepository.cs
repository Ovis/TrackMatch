using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 候補ペアの詳細Fingerprint比較結果を永続化する境界を定義する。
/// </summary>
public interface ICandidateComparisonRepository
{
    Task ReplaceAllAsync(
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定した比較結果だけを追加または更新する。
    /// </summary>
    /// <remarks>
    /// Fingerprintが変化したペアだけを再比較できるようにするための増分更新境界である。
    /// 更新対象ペアの既存分類は比較値と整合しなくなるため、永続化実装側で無効化する。
    /// </remarks>
    Task UpsertAsync(
        IReadOnlyCollection<CandidateComparison> comparisons,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidateComparison>> GetAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存済み比較結果の比較日時をペアごとに取得する。
    /// </summary>
    Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(
        CancellationToken cancellationToken = default);
}
