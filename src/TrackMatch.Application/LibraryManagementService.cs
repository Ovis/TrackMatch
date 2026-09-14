using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Libraries;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// GUIから利用するLibrary/Root管理操作を1つの境界へ集約する。
/// </summary>
public sealed class LibraryManagementService
{
    private readonly string _databasePath;
    private readonly string? _trashRoot;

    /// <summary>
    /// Library管理Serviceを生成する。
    /// </summary>
    /// <param name="databasePath">TrackMatch SQLite DB Path</param>
    /// <param name="trashRoot">設定済みApp-wide Trash Root。未設定ならnull</param>
    public LibraryManagementService(string databasePath, string? trashRoot = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException("Database path is required.", nameof(databasePath))
            : databasePath;
        _trashRoot = string.IsNullOrWhiteSpace(trashRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trashRoot));
    }

    /// <summary>
    /// 同じ管理画面からGlobal Track管理Serviceを生成するためのDB Pathを公開する。
    /// </summary>
    public string DatabasePath => _databasePath;

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
        ValidateAgainstTrash(rootPaths);
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
    /// Trash Rootとの配置を検証してLibraryへRootを追加する。
    /// </summary>
    public async Task<LibraryRoot> AddRootAsync(long libraryId, string rootPath, CancellationToken cancellationToken = default)
    {
        ValidateAgainstTrash([rootPath]);
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRepository(database).AddRootAsync(libraryId, rootPath, cancellationToken);
    }

    /// <summary>
    /// Root削除前に確認表示へ使うMembership件数を取得する。
    /// </summary>
    public async Task<long> GetRootTrackCountAsync(long rootId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryStatisticsRepository(database).GetRootTrackCountAsync(rootId, cancellationToken);
    }

    /// <summary>
    /// Library削除前に確認表示へ使うRoot数とMembership Track数を取得する。
    /// </summary>
    public async Task<LibraryDeleteSummary> GetDeleteSummaryAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryStatisticsRepository(database).GetLibraryDeleteSummaryAsync(libraryId, cancellationToken);
    }

    /// <summary>
    /// RootとそのLibrary Membershipを削除する。Global Trackと元Audio Fileは操作しない。
    /// </summary>
    public async Task RemoveRootAsync(long libraryId, long rootId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await new SqliteLibraryRepository(database).RemoveRootAsync(libraryId, rootId, cancellationToken);
    }

    /// <summary>
    /// LibraryとLibrary固有状態を削除する。Global Trackと元Audio Fileは操作しない。
    /// </summary>
    public async Task DeleteLibraryAsync(long libraryId, CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        await new SqliteLibraryRepository(database).DeleteAsync(libraryId, cancellationToken);
    }

    /// <summary>
    /// Trash Rootとの配置を検証し、Root保存場所変更の適用前Previewを作成する。
    /// </summary>
    public async Task<LibraryRootRemapPreview> PreviewRootRemapAsync(
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken = default)
    {
        ValidateAgainstTrash([newRootPath]);
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteLibraryRootRemapService(database)
            .PreviewAsync(libraryId, rootId, newRootPath, cancellationToken);
    }

    /// <summary>
    /// Trash Rootとの配置を再検証し、Preview済みGlobal Root relocationを適用する。
    /// </summary>
    /// <remarks>
    /// Remap本体のCommit後はTrackのMissing状態やMembershipが正本となるため、呼び出し元Cancelでは
    /// Keep Current State整理とGlobal Duplicate Group再同期を中断しない。
    /// </remarks>
    public async Task<LibraryRootRemapResult> RemapRootAsync(
        long libraryId,
        long rootId,
        string newRootPath,
        CancellationToken cancellationToken = default)
    {
        ValidateAgainstTrash([newRootPath]);
        var database = await OpenDatabaseAsync(cancellationToken);
        var result = await new SqliteLibraryRootRemapService(database)
            .ApplyAsync(libraryId, rootId, newRootPath, cancellationToken);

        // Remapでは複数LibraryのRoot/MembershipとGlobal Missingが同時に変わり得る。
        // Commit後に古いLibrary固有KeepやMaterialized Groupを残さないよう、正本から必ず再整合する。
        await new SqliteLibraryKeepStateMaintenance(database)
            .CleanupUnrelatedCurrentStatesAsync(CancellationToken.None);
        var reviews = new SqliteCandidateReviewRepository(database);
        var tracks = new SqliteTrackLookupRepository(database);
        var groups = new SqliteDuplicateGroupRepository(database);
        await new DuplicateGroupService(reviews, tracks, groups)
            .SynchronizeGlobalAsync(CancellationToken.None);

        return result;
    }

    private void ValidateAgainstTrash(IEnumerable<string> rootPaths)
    {
        if (_trashRoot is not null)
        {
            TrashPathRules.ValidateRootSeparation(_trashRoot, rootPaths);
        }
    }

    private async Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken);
        return database;
    }
}
