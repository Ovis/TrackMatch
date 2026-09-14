using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Root Remap Commit後にGlobal Missing、Duplicate Group、Library固有Keepが再整合されることを検証する。
/// </summary>
public sealed class LibraryManagementRemapConsistencyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private string _databasePath = null!;
    private SqliteDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "trackmatch.db");
        _database = new SqliteDatabase(_databasePath);
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RemapRootAsync_MissingTrackIsRemovedFromMaterializedDuplicateGroup()
    {
        var oldRoot = Path.Combine(_directory, "Old");
        var newRoot = Path.Combine(_directory, "New");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        var oldA = Path.Combine(oldRoot, "a.flac");
        var oldB = Path.Combine(oldRoot, "b.flac");
        var newB = Path.Combine(newRoot, "b.flac");
        await File.WriteAllBytesAsync(oldA, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(oldB, [2], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newB, [2], TestContext.Current.CancellationToken);

        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("Music", [oldRoot], TestContext.Current.CancellationToken);
        var root = Assert.Single(library.Roots);
        var tracks = new SqliteTrackRepository(_database);
        var a = await AddTrackAsync(tracks, library.Id, root.Id, oldA, "a.flac");
        var b = await AddTrackAsync(tracks, library.Id, root.Id, oldB, "b.flac");
        var duplicateGroups = CreateDuplicateGroupService();
        await duplicateGroups.SaveReviewAsync(
            library.Id,
            new CandidateReview(CandidatePairKey.Create(a, b), CandidateReviewDecision.ConfirmedDuplicate, null),
            a,
            TestContext.Current.CancellationToken);
        Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetAllGlobalAsync(TestContext.Current.CancellationToken));

        await new LibraryManagementService(_databasePath).RemapRootAsync(
            library.Id,
            root.Id,
            newRoot,
            TestContext.Current.CancellationToken);

        var newA = Path.Combine(newRoot, "a.flac");
        var remappedA = Assert.IsType<TrackMatch.Core.Persistence.StoredTrack>(
            await tracks.GetByPathAsync(newA, TestContext.Current.CancellationToken));
        var remappedB = Assert.IsType<TrackMatch.Core.Persistence.StoredTrack>(
            await tracks.GetByPathAsync(newB, TestContext.Current.CancellationToken));
        Assert.True(remappedA.IsMissing);
        Assert.False(remappedB.IsMissing);
        Assert.Empty(await new SqliteDuplicateGroupRepository(_database)
            .GetAllGlobalAsync(TestContext.Current.CancellationToken));
        Assert.Single(await new SqliteCandidateReviewRepository(_database)
            .GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemapRootAsync_ArchivesAndRemovesKeepStateForLibraryThatLosesAllGroupMembership()
    {
        var parentRoot = Path.Combine(_directory, "Parent");
        var oldChildRoot = Path.Combine(parentRoot, "Child");
        var newChildRoot = Path.Combine(_directory, "MovedChild");
        Directory.CreateDirectory(oldChildRoot);
        Directory.CreateDirectory(newChildRoot);
        var oldA = Path.Combine(oldChildRoot, "a.flac");
        var oldB = Path.Combine(oldChildRoot, "b.flac");
        var newA = Path.Combine(newChildRoot, "a.flac");
        var newB = Path.Combine(newChildRoot, "b.flac");
        await File.WriteAllBytesAsync(newA, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newB, [2], TestContext.Current.CancellationToken);

        var libraries = new SqliteLibraryRepository(_database);
        var parentLibrary = await libraries.CreateAsync("Parent", [parentRoot], TestContext.Current.CancellationToken);
        var childLibrary = await libraries.CreateAsync("Child", [oldChildRoot], TestContext.Current.CancellationToken);
        var parentRootId = Assert.Single(parentLibrary.Roots).Id;
        var childRootId = Assert.Single(childLibrary.Roots).Id;
        var tracks = new SqliteTrackRepository(_database);
        var a = await tracks.UpsertMetadataAsync(CreateMetadata(oldA), TestContext.Current.CancellationToken);
        var b = await tracks.UpsertMetadataAsync(CreateMetadata(oldB), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(childLibrary.Id, childRootId, a, "a.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(childLibrary.Id, childRootId, b, "b.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(parentLibrary.Id, parentRootId, a, Path.Combine("Child", "a.flac"), TestContext.Current.CancellationToken);

        var duplicateGroups = CreateDuplicateGroupService();
        await duplicateGroups.SaveReviewAsync(
            childLibrary.Id,
            new CandidateReview(CandidatePairKey.Create(a, b), CandidateReviewDecision.ConfirmedDuplicate, null),
            a,
            TestContext.Current.CancellationToken);
        var groupRepository = new SqliteDuplicateGroupRepository(_database);
        var parentProjection = Assert.Single(await groupRepository.GetByLibraryIdAsync(parentLibrary.Id, TestContext.Current.CancellationToken));
        await groupRepository.SetKeepAsync(
            parentLibrary.Id,
            parentProjection.Id,
            b,
            "UserSelected",
            TestContext.Current.CancellationToken);

        await new LibraryManagementService(_databasePath).RemapRootAsync(
            childLibrary.Id,
            childRootId,
            newChildRoot,
            TestContext.Current.CancellationToken);

        Assert.False(await new SqliteTrackLookupRepository(_database)
            .IsInLibraryAsync(a, parentLibrary.Id, TestContext.Current.CancellationToken));

        await using (var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            var history = await connection.QuerySingleAsync<(long? KeepTrackId, string Status, string ChangeKind)>(
                """
                SELECT KeepTrackId, Status, ChangeKind
                FROM LibraryDuplicateGroupKeepHistory
                WHERE LibraryId = @LibraryId AND ChangeKind = 'ScopeRemoved'
                ORDER BY Id DESC LIMIT 1;
                """,
                new { LibraryId = parentLibrary.Id });
            Assert.Equal(b, history.KeepTrackId);
            Assert.Equal(nameof(DuplicateGroupKeepStatus.Selected), history.Status);
            Assert.Equal("ScopeRemoved", history.ChangeKind);
        }

        // 後から再び同GroupへMembershipしても、Remap前のKeepがCurrent Stateとして復活してはいけない。
        var newParentRoot = await libraries.AddRootAsync(parentLibrary.Id, newChildRoot, TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(parentLibrary.Id, newParentRoot.Id, a, "a.flac", TestContext.Current.CancellationToken);
        var restoredProjection = Assert.Single(await groupRepository.GetByLibraryIdAsync(
            parentLibrary.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Unselected, restoredProjection.KeepStatus);
        Assert.Null(restoredProjection.KeepTrackId);
    }

    private DuplicateGroupService CreateDuplicateGroupService()
        => new(
            new SqliteCandidateReviewRepository(_database),
            new SqliteTrackLookupRepository(_database),
            new SqliteDuplicateGroupRepository(_database));

    private async Task<long> AddTrackAsync(
        SqliteTrackRepository tracks,
        long libraryId,
        long rootId,
        string path,
        string relativePath)
    {
        var id = await tracks.UpsertMetadataAsync(CreateMetadata(path), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(libraryId, rootId, id, relativePath, TestContext.Current.CancellationToken);
        return id;
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            100,
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(3),
            ["Artist"],
            Path.GetFileNameWithoutExtension(path),
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
