using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Global Duplicate GroupのTopology変更が、関係するLibrary Keep履歴だけへ反映されることを検証する。
/// </summary>
public sealed class DuplicateGroupHistorySemanticsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private long _libraryId;
    private long _rootId;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        var library = await new SqliteLibraryRepository(_database).CreateAsync(
            "Test Library",
            [_directory],
            TestContext.Current.CancellationToken);
        _libraryId = library.Id;
        _rootId = Assert.Single(library.Roots).Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Rebuild_LeavesUnrelatedGroupKeepStateAndHistoryUntouched()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        var d = await CreateTrackAsync(tracks, "d.flac");
        var service = CreateService();

        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, c, d, c);

        var groups = await new SqliteDuplicateGroupRepository(_database)
            .GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken);
        var unrelatedGroup = Assert.Single(groups, group => group.GlobalTrackIds.Contains(c));

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var beforeUpdatedAt = await connection.ExecuteScalarAsync<long>(
            """
            SELECT UpdatedAtUtcTicks
            FROM LibraryDuplicateGroupKeepStates
            WHERE LibraryId = @LibraryId AND DuplicateGroupId = @GroupId;
            """,
            new { LibraryId = _libraryId, GroupId = unrelatedGroup.Id });
        var beforeHistoryCount = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM LibraryDuplicateGroupKeepHistory
            WHERE LibraryId = @LibraryId AND DuplicateGroupId = @GroupId;
            """,
            new { LibraryId = _libraryId, GroupId = unrelatedGroup.Id });

        // A-Bだけを解除する。C-Dは同じGroup ID・Track集合・Keepのままなので履歴へ触れてはいけない。
        await service.DeleteReviewAsync(
            _libraryId,
            CandidatePairKey.Create(a, b),
            TestContext.Current.CancellationToken);

        var afterUpdatedAt = await connection.ExecuteScalarAsync<long>(
            """
            SELECT UpdatedAtUtcTicks
            FROM LibraryDuplicateGroupKeepStates
            WHERE LibraryId = @LibraryId AND DuplicateGroupId = @GroupId;
            """,
            new { LibraryId = _libraryId, GroupId = unrelatedGroup.Id });
        var afterHistoryCount = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM LibraryDuplicateGroupKeepHistory
            WHERE LibraryId = @LibraryId AND DuplicateGroupId = @GroupId;
            """,
            new { LibraryId = _libraryId, GroupId = unrelatedGroup.Id });

        Assert.Equal(beforeUpdatedAt, afterUpdatedAt);
        Assert.Equal(beforeHistoryCount, afterHistoryCount);
    }

    [Fact]
    public async Task SynchronizeGlobalAsync_RecordsNonKeepTrackMissingAndRestoreSemantics()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        var service = CreateService();
        var groups = new SqliteDuplicateGroupRepository(_database);

        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, b, c, b);
        var originalGroup = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetKeepAsync(_libraryId, originalGroup.Id, a, "UserSelected", TestContext.Current.CancellationToken);

        // KeepではないCだけがMissingになってGroupが縮退するケースでも、単なるGroupRebuildではなく原因を履歴化する。
        await tracks.MarkMissingAsync(c, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var reduced = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b }.Order().ToArray(), reduced.GlobalTrackIds.Order().ToArray());
        Assert.Equal(a, reduced.KeepTrackId);

        await using (var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            var kinds = (await connection.QueryAsync<string>(
                "SELECT ChangeKind FROM LibraryDuplicateGroupKeepHistory ORDER BY Id;"))
                .ToArray();
            Assert.Contains("TrackMissing", kinds);
        }

        // 同じTrackが再発見されても旧Dispositionは自動適用しない。
        // Trashから手動復元したファイルを再び無確認でTrash対象にしないため、Current Keepは要確認へ戻す。
        var restoredTrackId = await tracks.UpsertMetadataAsync(CreateMetadata("c.flac"), TestContext.Current.CancellationToken);
        Assert.Equal(c, restoredTrackId);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), restored.GlobalTrackIds.Order().ToArray());
        Assert.Equal(DuplicateGroupKeepStatus.Unselected, restored.KeepStatus);
        Assert.Null(restored.KeepTrackId);

        await using var verifyConnection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var changeKinds = (await verifyConnection.QueryAsync<string>(
            "SELECT ChangeKind FROM LibraryDuplicateGroupKeepHistory ORDER BY Id;"))
            .ToArray();
        Assert.Contains("TrackMissing", changeKinds);
        Assert.Contains("TrackRestored", changeKinds);
    }

    [Fact]
    public async Task RestoreAfterKeepChange_StillRequiresReviewBeforeOldTrashDispositionCanApply()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        var service = CreateService();
        var groups = new SqliteDuplicateGroupRepository(_database);

        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, b, c, b);
        var original = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetKeepAsync(_libraryId, original.Id, a, "UserSelected", TestContext.Current.CancellationToken);

        await tracks.MarkMissingAsync(c, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);
        var reduced = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));

        // Missing中の構成だけを見てKeepを変更しても、物理Track復帰というTopology遷移は消えない。
        // 復帰Trackを旧Dispositionで即Trashしないため、復帰後は必ず要確認へ戻す。
        await groups.SetKeepAsync(_libraryId, reduced.Id, b, "UserSelected", TestContext.Current.CancellationToken);
        var restoredId = await tracks.UpsertMetadataAsync(CreateMetadata("c.flac"), TestContext.Current.CancellationToken);
        Assert.Equal(c, restoredId);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Unselected, restored.KeepStatus);
        Assert.Null(restored.KeepTrackId);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var latestTopology = await connection.QuerySingleAsync<string>(
            """
            SELECT ChangeKind
            FROM LibraryDuplicateGroupKeepHistory
            WHERE ChangeKind IN ('TrackMissing', 'TrackRestored', 'GroupSplit', 'GroupMerge', 'GroupRebuild')
            ORDER BY Id DESC
            LIMIT 1;
            """);
        Assert.Equal("TrackRestored", latestTopology);
    }

    [Fact]
    public async Task RestoreAfterMissingSplitWithKeepOnNewGroupId_StillRequiresReview()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "split-a.flac");
        var b = await CreateTrackAsync(tracks, "split-b.flac");
        var c = await CreateTrackAsync(tracks, "split-c.flac");
        var d = await CreateTrackAsync(tracks, "split-d.flac");
        var e = await CreateTrackAsync(tracks, "split-e.flac");
        var service = CreateService();
        var groups = new SqliteDuplicateGroupRepository(_database);

        // A-B-C-D-Eの一本鎖を作り、KeepをEにする。
        // CがMissingになるとA-BとD-Eへ2:2でSplitし、旧Group IDは小さいTrack側A-Bへ継承される。
        // Keep=Eは新しいGroup IDのD-Eへ移るため、Restore判定をGroup IDだけへ結び付けると復帰を見失う。
        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, b, c, b);
        await SaveConfirmedAsync(service, c, d, d);
        await SaveConfirmedAsync(service, d, e, e);
        var original = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetKeepAsync(_libraryId, original.Id, e, "UserSelected", TestContext.Current.CancellationToken);

        await tracks.MarkMissingAsync(c, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var splitGroups = await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken);
        Assert.Equal(2, splitGroups.Count);
        var keepGroup = Assert.Single(splitGroups, group => group.GlobalTrackIds.Contains(e));
        Assert.Equal(e, keepGroup.KeepTrackId);
        Assert.NotEqual(original.Id, keepGroup.Id);

        var restoredId = await tracks.UpsertMetadataAsync(CreateMetadata("split-c.flac"), TestContext.Current.CancellationToken);
        Assert.Equal(c, restoredId);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Unselected, restored.KeepStatus);
        Assert.Null(restored.KeepTrackId);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        // Split後は複数の旧Component Current Stateが同じ復帰Topologyへ合流するため、
        // TrackRestored履歴自体は複数行になり得る。旧Keep=Eを保持していたSelected Stateが
        // 復帰として履歴化されたことを確認すれば、Q64の安全条件を直接検証できる。
        var selectedRestoreCount = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM LibraryDuplicateGroupKeepHistory
            WHERE ChangeKind = 'TrackRestored'
              AND Status = 'Selected'
              AND KeepTrackId = @KeepTrackId;
            """,
            new { KeepTrackId = e });
        Assert.Equal(1, selectedRestoreCount);
    }

    [Fact]
    public async Task ReAddingVerdictAfterOtherTopologyChange_DoesNotReuseOldTrackMissingAsRestoreReason()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        var service = CreateService();
        var groups = new SqliteDuplicateGroupRepository(_database);

        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, b, c, b);
        var originalGroup = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetKeepAsync(_libraryId, originalGroup.Id, a, "UserSelected", TestContext.Current.CancellationToken);

        await tracks.MarkMissingAsync(c, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);
        await tracks.UpsertMetadataAsync(CreateMetadata("c.flac"), TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        // 一度Missing→Restoreが完了した後、Human Verdict変更でCをGroupから外してから再度追加する。
        // 過去のTrackMissing履歴が残っていても、この再追加は物理Track復帰ではないためTrackRestoredを増やしてはいけない。
        await service.DeleteReviewAsync(
            _libraryId,
            CandidatePairKey.Create(b, c),
            TestContext.Current.CancellationToken);
        await SaveConfirmedAsync(service, b, c, b);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var restoredCount = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory WHERE ChangeKind = 'TrackRestored';");
        Assert.Equal(1, restoredCount);
    }

    private DuplicateGroupService CreateService()
        => new(
            new SqliteCandidateReviewRepository(_database),
            new SqliteTrackLookupRepository(_database),
            new SqliteDuplicateGroupRepository(_database));

    private async Task SaveConfirmedAsync(
        DuplicateGroupService service,
        long left,
        long right,
        long keepTrackId)
    {
        await service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, null),
            keepTrackId,
            TestContext.Current.CancellationToken);
    }

    private async Task<long> CreateTrackAsync(SqliteTrackRepository repository, string fileName)
    {
        var trackId = await repository.UpsertMetadataAsync(CreateMetadata(fileName), TestContext.Current.CancellationToken);
        await repository.EnsureMembershipAsync(
            _libraryId,
            _rootId,
            trackId,
            fileName,
            TestContext.Current.CancellationToken);
        return trackId;
    }

    private AudioTrackMetadata CreateMetadata(string fileName)
        => new(
            Path.Combine(_directory, fileName),
            1024,
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(3),
            ["Artist"],
            fileName,
            "Album",
            1,
            1,
            ["J-POPS"],
            "FLAC",
            "FLAC",
            900,
            44100,
            16,
            2,
            2026);
}
