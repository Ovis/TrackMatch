using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Candidate生成・比較用RepositoryがLibrary Membership境界を越えないことを実SQLiteで検証する。
/// </summary>
public sealed class LibraryScopedCandidatePersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private long _libraryAId;
    private long _libraryBId;
    private long _rootAId;
    private long _rootBId;
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
        _rootAId = Assert.Single(libraryA.Roots).Id;
        _rootBId = Assert.Single(libraryB.Roots).Id;

        var tracks = new SqliteTrackRepository(_database);
        _a1 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootA, "a1.flac")), TestContext.Current.CancellationToken);
        _a2 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootA, "a2.flac")), TestContext.Current.CancellationToken);
        _b1 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootB, "b1.flac")), TestContext.Current.CancellationToken);
        _b2 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(rootB, "b2.flac")), TestContext.Current.CancellationToken);

        // Global TrackのPathだけではLibrary Scopeは決まらない。候補系RepositoryはLibraryTracks Membershipを正本にする。
        await tracks.EnsureMembershipAsync(_libraryAId, _rootAId, _a1, "a1.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(_libraryAId, _rootAId, _a2, "a2.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(_libraryBId, _rootBId, _b1, "b1.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(_libraryBId, _rootBId, _b2, "b2.flac", TestContext.Current.CancellationToken);

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
    public async Task SegmentSketchRepository_MissingTrackCacheIsKeptButExcludedFromCandidateInput()
    {
        var options = new CandidateGenerationOptions();
        var fingerprints = await new SqliteFingerprintCatalogRepository(_database, _libraryAId)
            .GetActiveAsync(2, TestContext.Current.CancellationToken);
        var repository = new SqliteFingerprintSegmentSketchRepository(_database, _libraryAId);
        foreach (var fingerprint in fingerprints)
        {
            await repository.ReplaceTrackAsync(
                fingerprint,
                options,
                [new FingerprintSegmentSketch(fingerprint.TrackId, 0, unchecked((uint)fingerprint.TrackId))],
                TestContext.Current.CancellationToken);
        }

        await new SqliteTrackRepository(_database).MarkMissingAsync(_a2, TestContext.Current.CancellationToken);

        // Missing中もFingerprint未変更判定に使うSketch状態は保持するが、
        // Candidate探索入力にはActive TrackのSketchだけを渡して古い音声とのPair生成を防ぐ。
        var states = await repository.GetTrackStatesAsync(2, options, TestContext.Current.CancellationToken);
        var activeSketches = await repository.GetAllAsync(2, options, TestContext.Current.CancellationToken);

        Assert.Contains(_a2, states.Keys);
        var active = Assert.Single(activeSketches);
        Assert.Equal(_a1, active.TrackId);
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
    public async Task CandidatePairRepository_DeletePreservesPairOutsideCurrentLibrary()
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
    public async Task CandidatePairRepository_ReplaceForSharedTrackPreservesOtherLibraryPair()
    {
        var sharedRoot = Path.Combine(_directory, "Shared");
        Directory.CreateDirectory(sharedRoot);
        var libraries = new SqliteLibraryRepository(_database);
        var sharedRootA = await libraries.AddRootAsync(_libraryAId, sharedRoot, TestContext.Current.CancellationToken);
        var sharedRootB = await libraries.AddRootAsync(_libraryBId, sharedRoot, TestContext.Current.CancellationToken);
        var tracks = new SqliteTrackRepository(_database);
        var sharedPath = Path.Combine(sharedRoot, "shared.flac");
        var sharedTrackId = await tracks.UpsertMetadataAsync(CreateMetadata(sharedPath), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(_libraryAId, sharedRootA.Id, sharedTrackId, "shared.flac", TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(_libraryBId, sharedRootB.Id, sharedTrackId, "shared.flac", TestContext.Current.CancellationToken);

        var pairA = CandidatePairKey.Create(sharedTrackId, _a1);
        var pairB = CandidatePairKey.Create(sharedTrackId, _b1);
        var all = new SqliteCandidatePairRepository(_database);
        await all.ReplaceAllAsync(
            [
                new CandidatePair(pairA.TrackIdA, pairA.TrackIdB, 1),
                new CandidatePair(pairB.TrackIdA, pairB.TrackIdB, 2),
            ],
            TestContext.Current.CancellationToken);

        await new SqliteCandidatePairRepository(_database, _libraryAId).ReplaceForTracksAsync(
            [sharedTrackId],
            [new CandidatePair(pairA.TrackIdA, pairA.TrackIdB, 3)],
            TestContext.Current.CancellationToken);

        var remaining = await all.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, pair => CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB) == pairA);
        Assert.Contains(remaining, pair => CandidatePairKey.Create(pair.TrackIdA, pair.TrackIdB) == pairB);
    }

    [Fact]
    public async Task CandidatePairRepository_RegenerationPreservesReviewedPairAndComparison()
    {
        var pair = CandidatePairKey.Create(_a1, _a2);
        var pairs = new SqliteCandidatePairRepository(_database, _libraryAId);
        await pairs.ReplaceAllAsync(
            [new CandidatePair(pair.TrackIdA, pair.TrackIdB, 1)],
            TestContext.Current.CancellationToken);
        await new SqliteCandidateComparisonRepository(_database, _libraryAId).UpsertAsync(
            [CreateComparison(_a1, _a2, 0.99)],
            TestContext.Current.CancellationToken);
        await new SqliteCandidateReviewRepository(_database, _libraryAId).SaveAsync(
            new CandidateReview(pair, CandidateReviewDecision.NotDuplicate, null),
            TestContext.Current.CancellationToken);

        // Generation Version更新などで新GeneratorがこのPairを候補に返さなくても、
        // Human Verdictとそれを表示するMachine Current Stateは保持する。
        await pairs.ReplaceForTracksAsync(
            [_a1, _a2],
            [],
            TestContext.Current.CancellationToken);

        var persistedPair = Assert.Single(await pairs.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(pair, CandidatePairKey.Create(persistedPair.TrackIdA, persistedPair.TrackIdB));
        var persistedComparison = Assert.Single(await new SqliteCandidateComparisonRepository(_database, _libraryAId)
            .GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(pair, CandidatePairKey.Create(persistedComparison.TrackIdA, persistedComparison.TrackIdB));
        var report = Assert.Single(await new SqliteCandidateReviewReportRepository(_database)
            .GetAsync(_libraryAId, TestContext.Current.CancellationToken));
        Assert.Equal(CandidateReviewDecision.NotDuplicate, report.ReviewDecision);
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
