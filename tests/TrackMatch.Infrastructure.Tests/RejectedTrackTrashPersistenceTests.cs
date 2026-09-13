using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
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
    public async Task ProcessAsync_MovesFileMarksGlobalTrackMissingAndKeepsFingerprintCache()
    {
        var libraryRoot = Path.Combine(_directory, "Music");
        var trashRoot = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests.Trash", Guid.NewGuid().ToString("N"));
        var keepPath = Path.Combine(libraryRoot, "keep.flac");
        var sourcePath = Path.Combine(libraryRoot, "Album", "reject.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(keepPath, [9, 9, 9], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3], TestContext.Current.CancellationToken);

        try
        {
            var tracks = new SqliteTrackRepository(_database);
            var keepId = await tracks.UpsertMetadataAsync(CreateMetadata(keepPath), TestContext.Current.CancellationToken);
            var rejectId = await tracks.UpsertMetadataAsync(CreateMetadata(sourcePath), TestContext.Current.CancellationToken);
            await tracks.EnsureMembershipAsync(
                _libraryId,
                _rootId,
                keepId,
                Path.GetRelativePath(_directory, keepPath),
                TestContext.Current.CancellationToken);
            await tracks.EnsureMembershipAsync(
                _libraryId,
                _rootId,
                rejectId,
                Path.GetRelativePath(_directory, sourcePath),
                TestContext.Current.CancellationToken);
            await tracks.SaveFingerprintAsync(
                rejectId,
                new AudioFingerprint(sourcePath, TimeSpan.FromMinutes(4), [1u, 2u, 3u]),
                2,
                TestContext.Current.CancellationToken);

            var reviews = new SqliteCandidateReviewRepository(_database);
            var trackLookup = new SqliteTrackLookupRepository(_database);
            var groups = new SqliteDuplicateGroupRepository(_database);
            var groupService = new DuplicateGroupService(reviews, trackLookup, groups);
            await groupService.SaveReviewAsync(
                _libraryId,
                new CandidateReview(
                    CandidatePairKey.Create(keepId, rejectId),
                    CandidateReviewDecision.ConfirmedDuplicate,
                    null,
                    keepId),
                TestContext.Current.CancellationToken);

            var service = new RejectedTrackTrashService(
                groups,
                trackLookup,
                tracks,
                new LocalTrackFileOperations());

            var result = await service.ProcessAsync(
                _libraryId,
                trashRoot,
                execute: true,
                cancellationToken: TestContext.Current.CancellationToken);

            var item = Assert.Single(result.Items);
            Assert.Equal(RejectedTrackMoveStatus.Moved, item.Status);
            Assert.False(File.Exists(sourcePath));
            Assert.Equal(TrashPathRules.CreateDestinationPath(trashRoot, sourcePath), item.DestinationPath);
            Assert.True(File.Exists(item.DestinationPath));
            var stored = Assert.IsType<TrackMatch.Core.Persistence.StoredTrack>(
                await trackLookup.GetByIdAsync(rejectId, TestContext.Current.CancellationToken));
            Assert.True(stored.IsMissing);

            // TrashはContent Changeではないため、再利用可能なGlobal Fingerprintは保持する。
            Assert.NotNull(await tracks.GetFingerprintAsync(rejectId, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(trashRoot))
            {
                Directory.Delete(trashRoot, recursive: true);
            }
        }
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
