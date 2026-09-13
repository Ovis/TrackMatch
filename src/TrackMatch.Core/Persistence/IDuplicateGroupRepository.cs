using TrackMatch.Core.Duplicates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 確定済み重複グループを永続化・参照する境界を定義する。
/// </summary>
public interface IDuplicateGroupRepository
{
    /// <summary>指定Libraryの重複グループを取得する。</summary>
    Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(
        long libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>指定Trackが所属する重複グループを取得する。</summary>
    Task<DuplicateGroup?> GetByTrackIdAsync(
        long trackId,
        CancellationToken cancellationToken = default);

    /// <summary>指定IDの重複グループを取得する。</summary>
    Task<DuplicateGroup?> GetByIdAsync(
        long groupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定Libraryのグループ構成を再構成計画で置き換える。
    /// 既存IDが指定された項目はそのIDを維持する。
    /// </summary>
    Task ReplaceLibraryAsync(
        long libraryId,
        IReadOnlyCollection<DuplicateGroupRebuildItem> groups,
        CancellationToken cancellationToken = default);
}
