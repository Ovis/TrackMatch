using TrackMatch.Core.Candidates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 詳細比較へ渡す候補Trackペアの永続化境界を定義する。
/// </summary>
public interface ICandidatePairRepository
{
    Task ReplaceAllAsync(
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default);

    async Task ReplaceForTracksAsync(
        IReadOnlyCollection<long> trackIds,
        IReadOnlyCollection<CandidatePair> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        ArgumentNullException.ThrowIfNull(pairs);
        var affected = trackIds.ToHashSet();
        if (affected.Count == 0)
        {
            return;
        }

        var preserved = (await GetAllAsync(cancellationToken))
            .Where(pair => !affected.Contains(pair.TrackIdA) && !affected.Contains(pair.TrackIdB));
        await ReplaceAllAsync(preserved.Concat(pairs).ToArray(), cancellationToken);
    }

    async Task DeleteAsync(
        IReadOnlyCollection<CandidatePairKey> pairKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairKeys);
        if (pairKeys.Count == 0)
        {
            return;
        }

        var excluded = pairKeys.ToHashSet();
        var preserved = (await GetAllAsync(cancellationToken))
            .Where(pair => !excluded.Contains(CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB)))
            .ToArray();
        await ReplaceAllAsync(preserved, cancellationToken);
    }

    /// <summary>
    /// 指定Track集合の内部だけで成立するCandidate Pairを取得する。
    /// </summary>
    /// <remarks>
    /// Review後の局所再計算でGlobal Candidate全件を読み込まないための境界である。
    /// </remarks>
    Task<IReadOnlyList<CandidatePair>> GetWithinTracksAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定Pairが存在しない場合だけSupplemental Candidateとして追加する。
    /// </summary>
    /// <remarks>
    /// 既存の通常Candidateが持つ距離情報をSupplemental識別値で上書きしてはならない。
    /// </remarks>
    Task EnsureSupplementalAsync(
        CandidatePairKey pair,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定Track集合の内部だけを対象に、不要な未レビューSupplemental Candidateを削除する。
    /// </summary>
    Task DeleteObsoleteSupplementalWithinTracksAsync(
        IReadOnlyCollection<long> cleanupTrackIds,
        IReadOnlyCollection<CandidatePairKey> requiredPairs,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default);
}
