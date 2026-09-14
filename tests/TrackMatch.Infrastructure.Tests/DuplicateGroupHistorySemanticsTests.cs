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

        // 同じContentのTrackが再発見された場合は旧Verdictを維持したままGroupへ復帰し、復帰理由を履歴へ残す。
        await tracks.UpsertMetadataAsync(CreateMetadata("c.flac"), TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), restored.GlobalTrackIds.Order().ToArray());
        Assert.Equal(DuplicateGroupKeepStatus.Selected, restored.KeepStatus);
        Assert.Equal(a, restored.KeepTrackId);

        await using var verifyConnection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var changeKinds = (await verifyConnection.QueryAsync<string>(
            "SELECT ChangeKind FROM LibraryDuplicateGroupKeepHistory ORDER BY Id;"))
            .ToArray();
        Assert.Contains("TrackMissing", changeKinds);
        Assert.Contains("TrackRestored", changeKinds);
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
