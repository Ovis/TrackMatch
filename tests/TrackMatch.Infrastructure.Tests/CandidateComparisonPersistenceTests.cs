using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class CandidateComparisonPersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private long _trackIdA;
    private long _trackIdB;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        var trackRepository = new SqliteTrackRepository(_database);
        _trackIdA = await trackRepository.UpsertMetadataAsync(CreateMetadata("a.flac"), TestContext.Current.CancellationToken);
        _trackIdB = await trackRepository.UpsertMetadataAsync(CreateMetadata("b.flac"), TestContext.Current.CancellationToken);
        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync(
            [new CandidatePair(_trackIdA, _trackIdB, 1)],
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CandidateComparisonRepository_ReplacesMeasurementsAndCascadeDeletesThem()
    {
        var repository = new SqliteCandidateComparisonRepository(_database);
        var comparison = new CandidateComparison(
            _trackIdA,
            _trackIdB,
            0.987,
            -2,
            TimeSpan.FromMilliseconds(-250),
            100,
            TimeSpan.FromSeconds(12),
            0.9,
            0.8,
            0.95);

        await repository.ReplaceAllAsync([comparison], TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CandidateComparisons;");
        var similarity = await connection.ExecuteScalarAsync<double>("SELECT Similarity FROM CandidateComparisons;");
        Assert.Equal(1, count);
        Assert.Equal(0.987, similarity, 6);

        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync([], TestContext.Current.CancellationToken);
        count = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CandidateComparisons;");
        Assert.Equal(0, count);
    }

    private AudioTrackMetadata CreateMetadata(string fileName)
        => new(
            Path.Combine(_directory, fileName),
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
