namespace TrackMatch.Core.Persistence;

/// <summary>
/// Canonical Human Verdictに対してMaterialized Projectionを信用できるかを永続化する境界を定義する。
/// </summary>
public interface IProjectionStateRepository
{
    /// <summary>Global Duplicate GroupがDirtyか取得する。</summary>
    Task<bool> IsGlobalGroupsDirtyAsync(CancellationToken cancellationToken = default);

    /// <summary>指定LibraryのKeep ProjectionがDirtyか取得する。</summary>
    Task<bool> IsLibraryKeepProjectionDirtyAsync(
        long libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>Canonical Review Mutation開始直前にGlobalと現在Libraryを同一TransactionでDirty化する。</summary>
    Task MarkReviewMutationStartedAsync(
        long libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>指定LibraryのKeep Projection Dirty状態を更新する。</summary>
    Task SetLibraryKeepProjectionDirtyAsync(
        long libraryId,
        bool isDirty,
        CancellationToken cancellationToken = default);

    /// <summary>Global Duplicate Group Dirty状態を更新する。</summary>
    Task SetGlobalGroupsDirtyAsync(
        bool isDirty,
        CancellationToken cancellationToken = default);
}
