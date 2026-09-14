using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Global Verdict更新とSQLite上のGlobal Duplicate Group、Library固有Keepが一貫することを検証する。
/// </summary>
public sealed class DuplicateGroupPersistenceTests : IAsyncLifetime
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
        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("Test Library", [_directory], TestContext.Current.CancellationToken);
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
    public async Task SaveReview_AddsThirdTrackAndChangesLibraryKeep()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();

        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        var group = Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Selected, group.KeepStatus);
        Assert.Equal(b, group.KeepTrackId);
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), group.TrackIds);
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), group.GlobalTrackIds);
    }

    [Fact]
    public async Task DeleteReview_SplitsGroupAndLeavesRemainingProjectionWithValidKeep()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        await service.DeleteReviewAsync(
            _libraryId,
            CandidatePairKey.Create(b, c),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b }.Order().ToArray(), group.TrackIds);
        Assert.Equal(new[] { a, b }.Order().ToArray(), group.GlobalTrackIds);
        Assert.Equal(DuplicateGroupKeepStatus.Selected, group.KeepStatus);
        Assert.NotNull(group.KeepTrackId);
        Assert.Contains(group.KeepTrackId.Value, group.TrackIds);
        Assert.DoesNotContain(c, group.GlobalTrackIds);
    }

    [Fact]
    public async Task SaveReview_ContradictingNotDuplicateIsRejectedBeforePersistence()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(a, c), CandidateReviewDecision.NotDuplicate, null),
            TestContext.Current.CancellationToken));

        var reviews = await new SqliteCandidateReviewRepository(_database)
            .GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, reviews.Count);
        Assert.DoesNotContain(reviews, review => review.Pair == CandidatePairKey.Create(a, c));
    }

    [Fact]
    public async Task SetKeepAsync_AllowsGlobalGroupTrackOutsideCurrentLibrary()
    {
        var libraries = new SqliteLibraryRepository(_database);
        var secondRoot = Path.Combine(_directory, "second");
        Directory.CreateDirectory(secondRoot);
        var secondLibrary = await libraries.CreateAsync(
            "Second Library",
            [secondRoot],
            TestContext.Current.CancellationToken);
        var secondRootId = Assert.Single(secondLibrary.Roots).Id;
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await tracks.UpsertMetadataAsync(CreateMetadata("c.flac"), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(
            secondLibrary.Id,
            secondRootId,
            b,
            "b.flac",
            TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(
            secondLibrary.Id,
            secondRootId,
            c,
            "c.flac",
            TestContext.Current.CancellationToken);

        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(secondLibrary.Id, Confirmed(b, c, b), TestContext.Current.CancellationToken);
        var repository = new SqliteDuplicateGroupRepository(_database);
        var firstProjection = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(c, firstProjection.TrackIds);
        Assert.Contains(c, firstProjection.GlobalTrackIds);

        await repository.SetKeepAsync(
            _libraryId,
            firstProjection.Id,
            c,
            "UserSelected",
            TestContext.Current.CancellationToken);

        var updated = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Selected, updated.KeepStatus);
        Assert.Equal(c, updated.KeepTrackId);
        Assert.DoesNotContain(c, updated.TrackIds);
    }

    [Fact]
    public async Task SetKeepAsync_RejectsLibraryThatHasNoMembershipInGroup()
    {
        var (a, b, _) = await CreateTracksAsync();
        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);

        var libraries = new SqliteLibraryRepository(_database);
        var unrelatedRoot = Path.Combine(_directory, "unrelated");
        Directory.CreateDirectory(unrelatedRoot);
        var unrelatedLibrary = await libraries.CreateAsync(
            "Unrelated Library",
            [unrelatedRoot],
            TestContext.Current.CancellationToken);
        var repository = new SqliteDuplicateGroupRepository(_database);
        var group = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SetKeepAsync(
            unrelatedLibrary.Id,
            group.Id,
            a,
            "UserSelected",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SynchronizeGlobalAsync_RecordsMissingAndRestoreButRequiresKeepReviewAfterRestore()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();
        var repository = new SqliteDuplicateGroupRepository(_database);
        var tracks = new SqliteTrackRepository(_database);
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);
        var group = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await repository.SetKeepAsync(_libraryId, group.Id, a, "UserSelected", TestContext.Current.CancellationToken);

        await tracks.MarkMissingAsync(a, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var missingProjection = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { b, c }.Order().ToArray(), missingProjection.GlobalTrackIds);
        Assert.Equal(DuplicateGroupKeepStatus.Missing, missingProjection.KeepStatus);
        Assert.Null(missingProjection.KeepTrackId);

        await tracks.UpsertMetadataAsync(CreateMetadata("a.flac"), TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), restored.GlobalTrackIds);
        Assert.Equal(DuplicateGroupKeepStatus.Unselected, restored.KeepStatus);
        Assert.Null(restored.KeepTrackId);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var changeKinds = (await connection.QueryAsync<string>(
            "SELECT ChangeKind FROM LibraryDuplicateGroupKeepHistory ORDER BY Id;"))
            .ToArray();
        Assert.Contains("KeepMissing", changeKinds);
        Assert.Contains("KeepRestored", changeKinds);
    }

    [Fact]
    public async Task ReplaceGlobalAsync_DoesNotRestoreKeepForLibraryUnrelatedToSplitComponent()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();
        var repository = new SqliteDuplicateGroupRepository(_database);
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);
        var oldGroup = Assert.Single(await repository.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await repository.SetKeepAsync(_libraryId, oldGroup.Id, b, "UserSelected", TestContext.Current.CancellationToken);

        await using (var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            // Split後のB-C成分からLibrary Membershipをすべて外し、LibraryがAだけに関与する状態を再現する。
            await connection.ExecuteAsync(
                "DELETE FROM LibraryTracks WHERE LibraryId = @LibraryId AND TrackId IN @TrackIds;",
                new { LibraryId = _libraryId, TrackIds = new[] { b, c } });
        }

        await repository.ReplaceGlobalAsync(
            [new DuplicateGroupRebuildItem(oldGroup.Id, [b, c])],
            TestContext.Current.CancellationToken);

        await using var verifyConnection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var currentKeepCount = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM LibraryDuplicateGroupKeepStates WHERE LibraryId = @LibraryId;",
            new { LibraryId = _libraryId });
        var historyCount = await verifyConnection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory WHERE LibraryId = @LibraryId;",
            new { LibraryId = _libraryId });

        Assert.Equal(0, currentKeepCount);
        Assert.True(historyCount > 0);
    }

    [Fact]
    public async Task SynchronizeGlobalAsync_UnchangedTopologyDoesNotAppendKeepHistory()
    {
        var (a, b, _) = await CreateTracksAsync();
        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var before = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory;");

        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var after = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory;");
        Assert.Equal(before, after);
    }

    private DuplicateGroupService CreateService()
    {
        var reviews = new SqliteCandidateReviewRepository(_database);
        var tracks = new SqliteTrackLookupRepository(_database);
        return new DuplicateGroupService(reviews, tracks, new SqliteDuplicateGroupRepository(_database));
    }

    private async Task<(long A, long B, long C)> CreateTracksAsync()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        return (a, b, c);
    }

    private async Task<long> CreateTrackAsync(SqliteTrackRepository tracks, string fileName)
    {
        var id = await tracks.UpsertMetadataAsync(CreateMetadata(fileName), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(
            _libraryId,
            _rootId,
            id,
            fileName,
            TestContext.Current.CancellationToken);
        return id;
    }

    private AudioTrackMetadata CreateMetadata(string fileName)
        => new(
            Path.Combine(_directory, fileName),
            1024,
            new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(3),
            ["Artist"],
            "Title",
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

    private static CandidateReview Confirmed(long left, long right, long keep)
        => new(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, null, keep);
}
