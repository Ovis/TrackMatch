using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Root RemapでTrack IDと解析・Reviewデータを維持しつつPathとMissing状態だけを更新することを検証する。
/// </summary>
public sealed class LibraryRootRemapPersistenceTests : IAsyncLifetime
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
    public async Task Remap_PreservesIdsFingerprintAndReviewWhileUpdatingMissingPaths()
    {
        var oldRoot = Path.Combine(_directory, "OldMusic");
        var newRoot = Path.Combine(_directory, "NewMusic");
        Directory.CreateDirectory(Path.Combine(oldRoot, "Album"));
        Directory.CreateDirectory(Path.Combine(newRoot, "Album"));

        var oldA = Path.Combine(oldRoot, "Album", "01.flac");
        var oldB = Path.Combine(oldRoot, "Album", "02.flac");
        await File.WriteAllBytesAsync(oldA, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(oldB, [2], TestContext.Current.CancellationToken);
        var newA = Path.Combine(newRoot, "Album", "01.flac");
        await File.WriteAllBytesAsync(newA, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(newRoot, "unknown.mp3"), [3], TestContext.Current.CancellationToken);

        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("Music", [oldRoot], TestContext.Current.CancellationToken);
        var root = Assert.Single(library.Roots);
        var tracks = new SqliteTrackRepository(_database);
        var trackA = await tracks.UpsertMetadataAsync(CreateMetadata(oldA), TestContext.Current.CancellationToken);
        var trackB = await tracks.UpsertMetadataAsync(CreateMetadata(oldB), TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(trackA, CreateFingerprint(oldA), 2, TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(trackB, CreateFingerprint(oldB), 2, TestContext.Current.CancellationToken);

        var reviews = new SqliteCandidateReviewRepository(_database);
        await reviews.SaveAsync(
            new CandidateReview(
                CandidatePairKey.Create(trackA, trackB),
                CandidateReviewDecision.ConfirmedDuplicate,
                "keep",
                trackA),
            TestContext.Current.CancellationToken);

        // 旧Storageが既に取り外されていても、DBのRelativePathだけでRemapできる必要がある。
        Directory.Delete(oldRoot, recursive: true);
        var service = new SqliteLibraryRootRemapService(_database);
        var preview = await service.PreviewAsync(library.Id, root.Id, newRoot, TestContext.Current.CancellationToken);

        Assert.Equal(2, preview.TargetTrackCount);
        Assert.Equal(1, preview.MatchedTrackCount);
        Assert.Equal([Path.Combine("Album", "02.flac")], preview.MissingRelativePaths);
        Assert.Equal(1, preview.UnknownAudioFileCount);

        var result = await service.ApplyAsync(library.Id, root.Id, newRoot, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TargetTrackCount);
        Assert.Equal(1, result.MatchedTrackCount);
        Assert.Equal(1, result.MissingTrackCount);
        Assert.Equal(1, result.UnknownAudioFileCount);

        var remappedLibrary = Assert.IsType<TrackMatch.Core.Libraries.Library>(
            await libraries.GetAsync(library.Id, TestContext.Current.CancellationToken));
        var remappedRoot = Assert.Single(remappedLibrary.Roots);
        Assert.Equal(root.Id, remappedRoot.Id);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(newRoot)), remappedRoot.Path, ignoreCase: true);

        var remappedA = Assert.IsType<TrackMatch.Core.Persistence.StoredTrack>(
            await tracks.GetByPathAsync(newA, TestContext.Current.CancellationToken));
        var remappedBPath = Path.Combine(newRoot, "Album", "02.flac");
        var remappedB = Assert.IsType<TrackMatch.Core.Persistence.StoredTrack>(
            await tracks.GetByPathAsync(remappedBPath, TestContext.Current.CancellationToken));
        Assert.Equal(trackA, remappedA.Id);
        Assert.Equal(trackB, remappedB.Id);
        Assert.False(remappedA.IsMissing);
        Assert.True(remappedB.IsMissing);
        Assert.NotNull(await tracks.GetFingerprintAsync(trackA, TestContext.Current.CancellationToken));
        Assert.NotNull(await tracks.GetFingerprintAsync(trackB, TestContext.Current.CancellationToken));

        var review = Assert.Single(await reviews.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CandidatePairKey.Create(trackA, trackB), review.Pair);
        Assert.Equal(trackA, review.KeepTrackId);
    }

    [Fact]
    public async Task Preview_RejectsRootThatOverlapsAnotherRegisteredRoot()
    {
        var oldRoot = Path.Combine(_directory, "OldMusic");
        var otherRoot = Path.Combine(_directory, "OtherMusic");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(otherRoot);
        var nested = Path.Combine(otherRoot, "Nested");
        Directory.CreateDirectory(nested);

        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("Music", [oldRoot], TestContext.Current.CancellationToken);
        await libraries.CreateAsync("Other", [otherRoot], TestContext.Current.CancellationToken);
        var root = Assert.Single(library.Roots);

        var service = new SqliteLibraryRootRemapService(_database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(
            library.Id,
            root.Id,
            nested,
            TestContext.Current.CancellationToken));
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            1,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            Path.GetFileNameWithoutExtension(path),
            "Album",
            1,
            1,
            ["J-POPS"]);

    private static AudioFingerprint CreateFingerprint(string path)
        => new(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u, 4u]);
}
