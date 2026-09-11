using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class CandidateClassificationPersistenceTests : IAsyncLifetime
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
    public async Task ClassificationRepository_RoundTripsReportWithTrackMetadata()
    {
        var trackRepository = new SqliteTrackRepository(_database);
        var idA = await trackRepository.UpsertMetadataAsync(
            Metadata(Path.Combine(_directory, "A.flac"), "Artist A", "Title A", "Album A", "J-POPS"),
            TestContext.Current.CancellationToken);
        var idB = await trackRepository.UpsertMetadataAsync(
            Metadata(Path.Combine(_directory, "B.flac"), "Artist B", "Title B", "Album B", "Soundtrack"),
            TestContext.Current.CancellationToken);

        var pairRepository = new SqliteCandidatePairRepository(_database);
        await pairRepository.ReplaceAllAsync(
            [new CandidatePair(Math.Min(idA, idB), Math.Max(idA, idB), 1)],
            TestContext.Current.CancellationToken);

        var comparisonRepository = new SqliteCandidateComparisonRepository(_database);
        await comparisonRepository.ReplaceAllAsync(
            [new CandidateComparison(
                Math.Min(idA, idB),
                Math.Max(idA, idB),
                0.98,
                5,
                TimeSpan.FromSeconds(0.5),
                100,
                TimeSpan.FromSeconds(12.5),
                0.97,
                0.96,
                0.99)],
            TestContext.Current.CancellationToken);

        var repository = new SqliteCandidateClassificationRepository(_database);
        await repository.ReplaceAllAsync(
            [new CandidateClassification(
                Math.Min(idA, idB),
                Math.Max(idA, idB),
                AudioRelationshipKind.DuplicateCandidate,
                "test reason",
                "{\"test\":true}")],
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await repository.GetReportAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AudioRelationshipKind.DuplicateCandidate, row.Kind);
        Assert.Equal(0.98, row.Similarity, 6);
        Assert.Equal("Title A", row.TitleA);
        Assert.Equal("Title B", row.TitleB);
        Assert.Equal(["Artist A"], row.ArtistsA);
        Assert.Equal(["Soundtrack"], row.GenresB);
    }

    [Fact]
    public async Task ReviewReportRepository_ReturnsComparisonWithoutClassification()
    {
        var trackRepository = new SqliteTrackRepository(_database);
        var idA = await trackRepository.UpsertMetadataAsync(
            Metadata(Path.Combine(_directory, "unclassified-a.flac"), "Artist", "Same", "Album 1", "J-POPS"),
            TestContext.Current.CancellationToken);
        var idB = await trackRepository.UpsertMetadataAsync(
            Metadata(Path.Combine(_directory, "unclassified-b.flac"), "Artist", "Same", "Album 2", "J-POPS"),
            TestContext.Current.CancellationToken);
        var pair = CandidatePairKey.Create(idA, idB);

        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync(
            [new CandidatePair(pair.TrackIdA, pair.TrackIdB, 0)],
            TestContext.Current.CancellationToken);
        await new SqliteCandidateComparisonRepository(_database).ReplaceAllAsync(
            [new CandidateComparison(
                pair.TrackIdA,
                pair.TrackIdB,
                1.0,
                0,
                TimeSpan.Zero,
                100,
                TimeSpan.FromSeconds(12.5),
                1.0,
                1.0,
                1.0)],
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await new SqliteCandidateReviewReportRepository(_database)
            .GetAsync(TestContext.Current.CancellationToken));

        Assert.Null(row.Kind);
        Assert.Null(row.Reason);
        Assert.Equal(1.0, row.Similarity, 6);
        Assert.Equal("Same", row.TitleA);
        Assert.Equal("Same", row.TitleB);
    }

    private static AudioTrackMetadata Metadata(
        string path,
        string artist,
        string title,
        string album,
        string genre)
        => new(
            path,
            100,
            new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            [artist],
            title,
            album,
            1,
            1,
            [genre]);
}
