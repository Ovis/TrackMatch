using TrackMatch.Core.Libraries;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 論理LibraryとRootの永続化操作を提供する。
/// </summary>
public interface ILibraryRepository
{
    /// <summary>
    /// Library一覧をRoot込みで取得する。
    /// </summary>
    Task<IReadOnlyList<Library>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定したLibraryをRoot込みで取得する。
    /// </summary>
    Task<Library?> GetAsync(long libraryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 名前と1つ以上のRootを検証し、Libraryを一括作成する。
    /// </summary>
    Task<Library> CreateAsync(string name, IReadOnlyCollection<string> rootPaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Library名を変更する。
    /// </summary>
    Task RenameAsync(long libraryId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 既存LibraryへRootを追加する。
    /// </summary>
    Task<LibraryRoot> AddRootAsync(long libraryId, string rootPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 最後のRootを残す制約を守りつつRootを削除する。
    /// </summary>
    Task RemoveRootAsync(long libraryId, long rootId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Libraryと配下Rootを削除する。
    /// </summary>
    Task DeleteAsync(long libraryId, CancellationToken cancellationToken = default);
}
