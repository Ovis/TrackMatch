using TrackMatch.Core.Libraries;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// GUIから利用するLibrary/Root管理操作を1つの境界へ集約する。
/// </summary>
public sealed class LibraryManagementService(string databasePath)
{
    private readonly string _databasePath = string.IsNullOrWhiteSpace(databasePath)
        ? throw new ArgumentException("Database path is required.", nameof(databasePath))
        : databasePath;

    /// <summary>
    /// Library一覧をRoot込みで取得する。
    /// </summary>
    public async Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRepository(database).GetAllAsync(cancellationToken);
    }

    /// <summary>
    /// 名前と1つ以上のRootを検証してLibraryをAtomicに作成する。
    /// </summary>
    public async Task<Library> CreateLibraryAsync(
        string name,
        IReadOnlyCollection<string> rootPaths,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRepository(database).CreateAsync(name, rootPaths, cancellationToken);
    }

    /// <summary>
    /// Library名を変更する。
    /// </summary>
    public async Task RenameLibraryAsync(long libraryId, string name, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await new SqliteLibraryRepository(database).RenameAsync(libraryId, name, cancellationToken);
    }

    /// <summary>
    /// LibraryへRootを追加する。
    /// </summary>
    public async Task<LibraryRoot> AddRootAsync(long libraryId, string rootPath, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRepository(database).AddRootAsync(libraryId, rootPath, cancellationToken);
    }

    /// <summary>
    /// Root削除前に確認表示へ使うTrack件数を取得する。
    /// </summary>
    public async Task<long> GetRootTrackCountAsync(long rootId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryStatisticsRepository(database).GetRootTrackCountAsync(rootId, cancellationToken);
    }

    /// <summary>
    /// Library削除前に確認表示へ使うRoot数とTrack数を取得する。
    /// </summary>
    public async Task<LibraryDeleteSummary> GetDeleteSummaryAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryStatisticsRepository(database).GetLibraryDeleteSummaryAsync(libraryId, cancellationToken);
    }

    /// <summary>
    /// Rootと配下TrackMatch管理データを削除する。元Audio Fileは操作しない。
    /// </summary>
    public async Task RemoveRootAsync(long libraryId, long rootId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await new SqliteLibraryRepository(database).RemoveRootAsync(libraryId, rootId, cancellationToken);
    }

    /// <summary>
    /// Libraryと配下TrackMatch管理データを削除する。元Audio Fileは操作しない。
    /// </summary>
    public async Task DeleteLibraryAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await new SqliteLibraryRepository(database).DeleteAsync(libraryId, cancellationToken);
    }

    /// <summary>
    /// Root保存場所変更の適用前Previewを作成する。
    /// </summary>
    public async Task<LibraryRootRemapPreview> PreviewRootRemapAsync(
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRootRemapService(database)
            .PreviewAsync(libraryId, rootId, newRootPath, cancellationToken);
    }

    /// <summary>
    /// Preview済みRoot保存場所変更を適用する。
    /// </summary>
    public async Task<LibraryRootRemapResult> RemapRootAsync(
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRootRemapService(database)
            .ApplyAsync(libraryId, rootId, newRootPath, cancellationToken);
    }

    private async Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken);
        return database;
    }
}
