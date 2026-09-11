using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Candidate生成・比較用RepositoryがLibrary境界を越えないことを実SQLiteで検証する。
/// </summary>
public sealed class LibraryScopedCandidatePersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private long _libraryAId;
    private long _libraryBId;
    private long _a1;
    private long _a2;
    private long _b1;
    private long _b2;

    public async ValueTask InitializeAsync()
    {
        var rootA = Path.Combine(_directory, "LibraryA");
        var rootB = Path.Combine(_directory, "LibraryB");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);

        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        var libraries = new SqliteLibraryRepository(_database);
        var libraryA = await libraries.CreateAsync("Library A", [rootA], TestContext.Current.CancellationToken);
        var libraryB = await libraries.CreateAsync("Library B", [rootB], TestContext.Current.CancellationToken);
        _libraryAId = libraryA.Id;
        _libraryBId = libraryB.Id;

        var tracks = new SqliteTrackRepository(_database);
        _a1 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootA, "a1.flac")), TestContext.Current.CancellationToken);
        _a2 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootA, "a2.flac")), TestContext.Current.CancellationToken);
        _b1 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootB, "b1.flac")), TestContext.Current.CancellationToken);
        _b2 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootB, "b2.flac")), TestContext.Current.CancellationToken);

        await tracks.SaveFingerprintAsync(_a1, CreateFingerprint(Path.Combine(rootA, "a1.flac")), 2, TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(_a2, CreateFingerprint(Path.Combine(rootA, "a2.flac")), 2, TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(_b1, CreateFingerprint(Path.Combine(rootB, "b1.flac")), 2, TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(_b2, CreateFingerprint(Path.Combine(rootB, "b2.flac")), 2, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FingerprintCatalog_ReturnsOnlySelectedLibrary()
    {
        var fingerprints = await new SqliteFingerprintCatalogRepository(_database, _libraryAId)
            .GetActiveAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal([_a1, _a2], fingerprints.Select(item => item.TrackId));
    }

    [Fact]
    public async Task SegmentSketchRepository_ReturnsOnlySelectedLibrary()
    {
        var options = new CandidateGenerationOptions();
        var fingerprints = await new SqliteFingerprintCatalogRepository(_database)
            .GetActiveAsync(2, TestContext.Current.CancellationToken);
        var allSketches = new SqliteFingerprintSegmentSketchRepository(_database);
        foreach (var fingerprint in fingerprints)
        {
            await allSketches.ReplaceTrackAsync(
                fingerprint,
                options,
                [new FingerprintSegmentSketch(fingerprint.TrackId, 0, unchecked((uint)fingerprint.TrackId))],
                TestContext.Current.CancellationToken);
        }

        var libraryASketches = await new SqliteFingerprintSegmentSketchRepository(_database, _libraryAId)
            .GetAllAsync(2, options, TestContext.Current.CancellationToken);

        Assert.Equal([_a1, _a2], libraryASketches.Select(item => item.TrackId));
    }

    [Fact]
    public async Task CandidatePairRepository_ReplaceAllPreservesOtherLibraries()
    {
        var all = new SqliteCandidatePairRepository(_database);
        await all.ReplaceAllAsync(
            [new CandidatePair(_a1, _a2, 1), new CandidatePair(_b1, _b2, 2)],
            TestContext.Current.CancellationToken);

        await new SqliteCandidatePairRepository(_database, _libraryAId)
            .ReplaceAllAsync([], TestContext.Current.CancellationToken);

        var remaining = Assert.Single(await all.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CandidatePairKey.Create(_b1, _b2), CandidatePairKey.Create(remaining.TrackIdA, remaining.TrackIdB));
    }

    [Fact]
    public async Task CandidatePairRepository_DeletePreservesReviewedPairInOtherLibrary()
    {
        var all = new SqliteCandidatePairRepository(_database);
        var pairA = CandidatePairKey.Create(_a1, _a2);
        var pairB = CandidatePairKey.Create(_b1, _b2);
        await all.ReplaceAllAsync(
            [new CandidatePair(pairA.TrackIdA, pairA.TrackIdB, 1), new CandidatePair(pairB.TrackIdA, pairB.TrackIdB, 2)],
            TestContext.Current.CancellationToken);

        await new SqliteCandidatePairRepository(_database, _libraryAId)
            .DeleteAsync([pairA, pairB], TestContext.Current.CancellationToken);

        var remaining = Assert.Single(await all.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(pairB, CandidatePairKey.Create(remaining.TrackIdA, remaining.TrackIdB));
    }

    [Fact]
    public async Task CandidatePairRepository_RejectsCrossLibraryPair()
    {
        var repository = new SqliteCandidatePairRepository(_database, _libraryAId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReplaceAllAsync(
            [new CandidatePair(Math.Min(_a1, _b1), Math.Max(_a1, _b1), 1)],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CandidateComparisonRepository_ReadsOnlySelectedLibrary()
    {
        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync(
            [new CandidatePair(_a1, _a2, 1), new CandidatePair(_b1, _b2, 1)],
            TestContext.Current.CancellationToken);
        var all = new SqliteCandidateComparisonRepository(_database);
        await all.ReplaceAllAsync(
            [CreateComparison(_a1, _a2, 0.99), CreateComparison(_b1, _b2, 0.98)],
            TestContext.Current.CancellationToken);

        var libraryA = Assert.Single(await new SqliteCandidateComparisonRepository(_database, _libraryAId)
            .GetAllAsync(TestContext.Current.CancellationToken));
        var libraryB = Assert.Single(await new SqliteCandidateComparisonRepository(_database, _libraryBId)
            .GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(CandidatePairKey.Create(_a1, _a2), CandidatePairKey.Create(libraryA.TrackIdA, libraryA.TrackIdB));
        Assert.Equal(CandidatePairKey.Create(_b1, _b2), CandidatePairKey.Create(libraryB.TrackIdA, libraryB.TrackIdB));
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            100,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            "Title",
            "Album",
            1,
            1,
            ["J-POPS"]);

    private static AudioFingerprint CreateFingerprint(string path)
        => new(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u, 4u]);

    private static CandidateComparison CreateComparison(long trackIdA, long trackIdB, double similarity)
        => new(
            Math.Min(trackIdA, trackIdB),
            Math.Max(trackIdA, trackIdB),
            similarity,
            0,
            TimeSpan.Zero,
            4,
            TimeSpan.FromSeconds(1),
            1,
            1,
            1);
}
