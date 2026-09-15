using TrackMatch.Core.Duplicates;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Global Duplicate GroupとLibrary固有Keep Stateを永続化・参照する境界を定義する。
/// </summary>
public interface IDuplicateGroupRepository
{
    /// <summary>Global GroupのCurrent構成を取得する。</summary>
    Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Global Groupを指定LibraryへProjectionして取得する。</summary>
    Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(
        long libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>指定Trackが所属するGlobal GroupをLibraryへProjectionして取得する。</summary>
    Task<DuplicateGroup?> GetByTrackIdAsync(
        long trackId,
        long libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>指定Global GroupをLibraryへProjectionして取得する。</summary>
    Task<DuplicateGroup?> GetByIdAsync(
        long groupId,
        long libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Current Global Group構成を再構成計画で置き換え、既存Library Keep StateをMerge/Split規則に従って引き継ぐ。
    /// </summary>
    Task ReplaceGlobalAsync(
        IReadOnlyCollection<DuplicateGroupRebuildItem> groups,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定LibraryのGroup Keepを明示的に選択する。
    /// Library外TrackもGlobal Group構成TrackであればKeepにできる。
    /// </summary>
    Task SetKeepAsync(
        long libraryId,
        long groupId,
        long keepTrackId,
        string changeKind,
        CancellationToken cancellationToken = default);
}
