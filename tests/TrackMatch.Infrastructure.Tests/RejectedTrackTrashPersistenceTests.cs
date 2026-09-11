using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Trash;
using TrackMatch.Infrastructure.Persistence;
using TrackMatch.Infrastructure.Trash;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class RejectedTrackTrashPersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        await new SqliteLibraryRepository(_database).CreateAsync(
            "Test Library",
            [_directory],
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ProcessAsync_MovesFileAndUpdatesStoredTrack()
    {
        var libraryRoot = Path.Combine(_directory, "Music");
        var trashRoot = Path.Combine(_directory, "Trash");
        var sourcePath = Path.Combine(libraryRoot, "Album", "reject.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3], TestContext.Current.CancellationToken);

        var tracks = new SqliteTrackRepository(_database);
        var keepId = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(libraryRoot, "keep.flac")), TestContext.Current.CancellationToken);
        var rejectId = await tracks.UpsertMetadataAsync(CreateMetadata(sourcePath), TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(
            rejectId,
            new AudioFingerprint(sourcePath, TimeSpan.FromMinutes(4), [1u, 2u, 3u]),
            2,
            TestContext.Current.CancellationToken);
        var reviews = new SqliteCandidateReviewRepository(_database);
        await reviews.SaveAsync(
            new CandidateReview(
                CandidatePairKey.Create(keepId, rejectId),
                CandidateReviewDecision.ConfirmedDuplicate,
                null,
                keepId),
            TestContext.Current.CancellationToken);
        var service = new RejectedTrackTrashService(
            reviews,
            new SqliteTrackLookupRepository(_database),
            tracks,
            new LocalTrackFileOperations());

        var result = await service.ProcessAsync(
            libraryRoot,
            trashRoot,
            execute: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(RejectedTrackMoveStatus.Moved, Assert.Single(result.Items).Status);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(Path.Combine(trashRoot, "Album", "reject.flac")));
        var stored = Assert.IsType<TrackMatch.Core.Persistence.StoredTrack>(
            await new SqliteTrackLookupRepository(_database).GetByIdAsync(rejectId, TestContext.Current.CancellationToken));
        Assert.True(stored.IsMissing);
        Assert.Null(await tracks.GetFingerprintAsync(rejectId, TestContext.Current.CancellationToken));
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            100,
            new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            "Title",
            "Album",
            1,
            1,
            ["J-POPS"]);
}
