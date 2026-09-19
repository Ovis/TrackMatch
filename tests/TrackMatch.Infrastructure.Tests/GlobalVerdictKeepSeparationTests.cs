using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Preferred Trackを含むGlobal Human Verdictと派生Keepの一貫性を実SQLiteで検証する。
/// </summary>
public sealed class GlobalVerdictKeepSeparationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ChangingPreferredTrackFromAnotherLibraryChangesGlobalVerdictAndDerivedKeep()
    {
        var libraries = new SqliteLibraryRepository(_database);
        var source = await libraries.CreateAsync("Source Library", [_directory], TestContext.Current.CancellationToken);
        var viewer = await libraries.CreateAsync("Viewer Library", [_directory], TestContext.Current.CancellationToken);
        var sourceRoot = Assert.Single(source.Roots);
        var viewerRoot = Assert.Single(viewer.Roots);
        var tracks = new SqliteTrackRepository(_database);
        var trackA = await tracks.UpsertMetadataAsync(CreateMetadata("a.flac"), TestContext.Current.CancellationToken);
        var trackB = await tracks.UpsertMetadataAsync(CreateMetadata("b.flac"), TestContext.Current.CancellationToken);
        foreach (var (libraryId, rootId) in new[] { (source.Id, sourceRoot.Id), (viewer.Id, viewerRoot.Id) })
        {
            await tracks.EnsureMembershipAsync(libraryId, rootId, trackA, "a.flac", TestContext.Current.CancellationToken);
            await tracks.EnsureMembershipAsync(libraryId, rootId, trackB, "b.flac", TestContext.Current.CancellationToken);
        }

        var service = CreateService();
        var pair = CandidatePairKey.Create(trackA, trackB);
        await service.SaveReviewAsync(
            source.Id,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, trackA, null),
            TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(
            viewer.Id,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, trackB, null),
            TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var current = await connection.QuerySingleAsync<(long SourceLibraryId, long PreferredTrackId)>(
            "SELECT SourceLibraryId, PreferredTrackId FROM CandidateReviews WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            new { pair.TrackIdA, pair.TrackIdB });
        var historyCount = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM CandidateReviewHistory WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            new { pair.TrackIdA, pair.TrackIdB });

        Assert.Equal(viewer.Id, current.SourceLibraryId);
        Assert.Equal(trackB, current.PreferredTrackId);
        Assert.Equal(1, historyCount);

        var groups = new SqliteDuplicateGroupRepository(_database);
        var viewerGroup = Assert.Single(await groups.GetByLibraryIdAsync(viewer.Id, TestContext.Current.CancellationToken));
        Assert.Equal(trackB, viewerGroup.KeepTrackId);
    }

    [Fact]
    public async Task SameGlobalVerdictDoesNotAppendReviewOrKeepHistory()
    {
        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("Library", [_directory], TestContext.Current.CancellationToken);
        var root = Assert.Single(library.Roots);
        var tracks = new SqliteTrackRepository(_database);
        var trackA = await tracks.UpsertMetadataAsync(CreateMetadata("same-a.flac"), TestContext.Current.CancellationToken);
        var trackB = await tracks.UpsertMetadataAsync(CreateMetadata("same-b.flac"), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(library.Id, root.Id, trackA, "same-a.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(library.Id, root.Id, trackB, "same-b.flac", TestContext.Current.CancellationToken);

        var service = CreateService();
        var review = new CandidateReview(
            CandidatePairKey.Create(trackA, trackB),
            CandidateReviewDecision.ConfirmedDuplicate,
            trackA,
            null);
        await service.SaveReviewAsync(library.Id, review, TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var reviewHistoryBefore = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CandidateReviewHistory;");
        var keepHistoryBefore = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory;");

        await service.SaveReviewAsync(library.Id, review, TestContext.Current.CancellationToken);

        var reviewHistoryAfter = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CandidateReviewHistory;");
        var keepHistoryAfter = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory;");
        Assert.Equal(reviewHistoryBefore, reviewHistoryAfter);
        Assert.Equal(keepHistoryBefore, keepHistoryAfter);
    }

    private DuplicateGroupService CreateService()
        => new(
            new SqliteCandidateReviewRepository(_database),
            new SqliteTrackLookupRepository(_database),
            new SqliteDuplicateGroupRepository(_database));

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
            ["J-POPS"]);
}
