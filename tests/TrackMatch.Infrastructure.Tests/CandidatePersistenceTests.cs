using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class CandidatePersistenceTests : IAsyncLifetime
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
    public async Task FingerprintCatalog_ReturnsOnlyActiveTracksForAlgorithm()
    {
        var tracks = new SqliteTrackRepository(_database);
        var activePath = Path.Combine(_directory, "active.flac");
        var missingPath = Path.Combine(_directory, "missing.flac");
        var activeId = await tracks.UpsertMetadataAsync(CreateMetadata(activePath), TestContext.Current.CancellationToken);
        var missingId = await tracks.UpsertMetadataAsync(CreateMetadata(missingPath), TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(activeId, CreateFingerprint(activePath), 2, TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(missingId, CreateFingerprint(missingPath), 2, TestContext.Current.CancellationToken);
        await tracks.MarkMissingAsync(missingId, TestContext.Current.CancellationToken);

        var fingerprints = await new SqliteFingerprintCatalogRepository(_database)
            .GetActiveAsync(2, TestContext.Current.CancellationToken);

        var stored = Assert.Single(fingerprints);
        Assert.Equal(activeId, stored.TrackId);
    }

    [Fact]
    public async Task FingerprintSegmentSketchRepository_ReturnsStoredFingerprintTimestamp()
    {
        var tracks = new SqliteTrackRepository(_database);
        var path = Path.Combine(_directory, "sketch.flac");
        var trackId = await tracks.UpsertMetadataAsync(CreateMetadata(path), TestContext.Current.CancellationToken);
        var extractedAtUtc = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);
        var storedFingerprint = new StoredFingerprint(trackId, 2, CreateFingerprint(path), extractedAtUtc);
        var options = new CandidateGenerationOptions();
        var repository = new SqliteFingerprintSegmentSketchRepository(_database);

        await repository.ReplaceTrackAsync(
            storedFingerprint,
            options,
            [new FingerprintSegmentSketch(trackId, 0, 0x12345678u)],
            TestContext.Current.CancellationToken);

        var states = await repository.GetTrackStatesAsync(2, options, TestContext.Current.CancellationToken);

        Assert.Equal(extractedAtUtc, states[trackId]);
    }

    [Fact]
    public async Task CandidatePairRepository_ReplacesExistingPairs()
    {
        var tracks = new SqliteTrackRepository(_database);
        var id1 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(_directory, "1.flac")), TestContext.Current.CancellationToken);
        var id2 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(_directory, "2.flac")), TestContext.Current.CancellationToken);
        var id3 = await tracks.UpsertMetadataAsync(CreateMetadata(Path.Combine(_directory, "3.flac")), TestContext.Current.CancellationToken);
        var repository = new SqliteCandidatePairRepository(_database);

        await repository.ReplaceAllAsync(
            [new CandidatePair(id1, id2, 1), new CandidatePair(id1, id3, 2)],
            TestContext.Current.CancellationToken);
        await repository.ReplaceAllAsync(
            [new CandidatePair(id2, id3, 0)],
            TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var pairs = (await connection.QueryAsync<(long TrackIdA, long TrackIdB, int MinimumSegmentHashDistance)>(
            "SELECT TrackIdA, TrackIdB, MinimumSegmentHashDistance FROM CandidatePairs;"))
            .ToArray();
        var pair = Assert.Single(pairs);
        Assert.Equal((id2, id3, 0), pair);
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            100,
            new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            "Title",
            "Album",
            1,
            1,
            ["J-POPS"]);

    private static AudioFingerprint CreateFingerprint(string path)
        => new(path, TimeSpan.FromMinutes(4), [0x12345678u, 0x23456789u, 0x3456789au]);
}
