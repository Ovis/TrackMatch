using Microsoft.Data.Sqlite;
using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Global Track管理操作後にMaterialized Duplicate GroupがCurrent Verdictから再同期されることを検証する。
/// </summary>
public sealed class TrackManagementServiceConsistencyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private string _databasePath = null!;
    private SqliteDatabase _database = null!;
    private long _libraryId;
    private long _rootId;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "trackmatch.db");
        _database = new SqliteDatabase(_databasePath);
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
    public async Task DeleteTracksAsync_RemovingBridgeTrackRebuildsRemainingConnectedComponent()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        var d = await CreateTrackAsync(tracks, "d.flac");
        var duplicateGroups = new DuplicateGroupService(
            new SqliteCandidateReviewRepository(_database),
            new SqliteTrackLookupRepository(_database),
            new SqliteDuplicateGroupRepository(_database));

        await SaveConfirmedAsync(duplicateGroups, a, b, a);
        await SaveConfirmedAsync(duplicateGroups, b, c, b);
        await SaveConfirmedAsync(duplicateGroups, c, d, c);
        Assert.Equal(4, Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetAllGlobalAsync(TestContext.Current.CancellationToken)).TrackIds.Count);

        var deleted = await new TrackManagementService(_databasePath)
            .DeleteTracksAsync([b], TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        var remainingGroup = Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetAllGlobalAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { c, d }, remainingGroup.TrackIds.Order().ToArray());
        Assert.DoesNotContain(a, remainingGroup.TrackIds);
        Assert.DoesNotContain(b, remainingGroup.TrackIds);
    }

    private async Task<long> CreateTrackAsync(SqliteTrackRepository repository, string fileName)
    {
        var trackId = await repository.UpsertMetadataAsync(
            new AudioTrackMetadata(
                Path.Combine(_directory, fileName),
                1024,
                new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMinutes(3),
                ["Artist"],
                fileName,
                "Album",
                1,
                1,
                ["J-POPS"]),
            TestContext.Current.CancellationToken);
        await repository.EnsureMembershipAsync(
            _libraryId,
            _rootId,
            trackId,
            fileName,
            TestContext.Current.CancellationToken);
        return trackId;
    }

    private async Task SaveConfirmedAsync(
        DuplicateGroupService service,
        long left,
        long right,
        long keepTrackId)
    {
        await service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(
                CandidatePairKey.Create(left, right),
                CandidateReviewDecision.ConfirmedDuplicate,
                null),
            keepTrackId,
            TestContext.Current.CancellationToken);
    }
}
