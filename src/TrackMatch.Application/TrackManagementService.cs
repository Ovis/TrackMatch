using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Management;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// Global Track管理画面から利用する一覧取得、Force Reanalysis、管理データ削除を集約する。
/// </summary>
public sealed class TrackManagementService
{
    private readonly string _databasePath;

    /// <summary>
    /// Global Track管理Serviceを生成する。
    /// </summary>
    /// <param name="databasePath">TrackMatch SQLite DB Path</param>
    public TrackManagementService(string databasePath)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException("Database path is required.", nameof(databasePath))
            : databasePath;
    }

    /// <summary>
    /// 指定FilterのGlobal Track一覧を取得する。
    /// </summary>
    public async Task<IReadOnlyList<ManagedTrack>> GetTracksAsync(
        TrackManagementFilter filter,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        return await new SqliteTrackManagementRepository(database).GetTracksAsync(filter, cancellationToken);
    }

    /// <summary>
    /// 選択したGlobal Trackを1 TransactionでForce Reanalysisする。
    /// </summary>
    public async Task<ForceReanalysisResult> ForceReanalysisTracksAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var database = await OpenDatabaseAsync(cancellationToken);
        var result = await new SqliteTrackManagementRepository(database)
            .ForceReanalysisTracksAsync(trackIds, cancellationToken);
        await SynchronizeGroupsAfterCommitAsync(database);
        return result;
    }

    /// <summary>
    /// 指定Root由来MembershipのGlobal TrackをForce Reanalysisする。
    /// </summary>
    public async Task<ForceReanalysisResult> ForceReanalysisRootAsync(
        long rootId,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var result = await new SqliteTrackManagementRepository(database)
            .ForceReanalysisRootAsync(rootId, cancellationToken);
        await SynchronizeGroupsAfterCommitAsync(database);
        return result;
    }

    /// <summary>
    /// 指定Libraryに所属するGlobal TrackをForce Reanalysisする。
    /// </summary>
    public async Task<ForceReanalysisResult> ForceReanalysisLibraryAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        var database = await OpenDatabaseAsync(cancellationToken);
        var result = await new SqliteTrackManagementRepository(database)
            .ForceReanalysisLibraryAsync(libraryId, cancellationToken);
        await SynchronizeGroupsAfterCommitAsync(database);
        return result;
    }

    /// <summary>
    /// 選択したGlobal TrackのTrackMatch管理データを完全削除する。
    /// 元Audio Fileは操作しない。
    /// </summary>
    public async Task<int> DeleteTracksAsync(
        IReadOnlyCollection<long> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var database = await OpenDatabaseAsync(cancellationToken);
        var deleted = await new SqliteTrackManagementRepository(database)
            .DeleteTracksAsync(trackIds, cancellationToken);
        await SynchronizeGroupsAfterCommitAsync(database);
        return deleted;
    }

    private static async Task SynchronizeGroupsAfterCommitAsync(SqliteDatabase database)
    {
        // Repository TransactionがCommitされた後はCurrent Verdict/Track状態が正本として確定している。
        // 呼び出し元のCancelをここへ伝播するとMaterialized Duplicate Groupだけ旧状態で残るため、
        // Scanキャンセル時と同様に整合同期だけはキャンセル不可で完了させる。
        var service = new DuplicateGroupService(
            new SqliteCandidateReviewRepository(database),
            new SqliteTrackLookupRepository(database),
            new SqliteDuplicateGroupRepository(database));
        await service.SynchronizeGlobalAsync(CancellationToken.None);
    }

    private async Task<SqliteDatabase> OpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = new SqliteDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken);
        return database;
    }
}
