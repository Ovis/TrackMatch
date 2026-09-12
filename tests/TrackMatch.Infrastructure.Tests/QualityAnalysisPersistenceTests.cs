using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Models;
using TrackMatch.Core.Quality;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// 音質解析キャッシュの永続化とCascade削除を検証する。
/// </summary>
public sealed class QualityAnalysisPersistenceTests : IAsyncLifetime
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
        await new SqliteLibraryRepository(_database).CreateAsync(
            "Test Library",
            [_directory],
            TestContext.Current.CancellationToken);

        var trackRepository = new SqliteTrackRepository(_database);
        _trackIdA = await trackRepository.UpsertMetadataAsync(CreateMetadata("a.flac"), TestContext.Current.CancellationToken);
        _trackIdB = await trackRepository.UpsertMetadataAsync(CreateMetadata("b.flac"), TestContext.Current.CancellationToken);
        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync(
            [new CandidatePair(_trackIdA, _trackIdB, 1)],
            TestContext.Current.CancellationToken);
        await new SqliteCandidateComparisonRepository(_database).ReplaceAllAsync(
            [new CandidateComparison(
                _trackIdA,
                _trackIdB,
                0.99,
                0,
                TimeSpan.Zero,
                100,
                TimeSpan.FromSeconds(10),
                0.9,
                0.9,
                1.0)],
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TrackQualityAnalysisRepository_RoundTripsValues()
    {
        var repository = new SqliteTrackQualityAnalysisRepository(_database);
        var analyzedAt = new DateTime(2026, 9, 12, 1, 2, 3, DateTimeKind.Utc);
        var expected = new TrackQualityAnalysis(
            _trackIdA,
            3,
            QualityAnalysisStatus.Analyzed,
            -11.837421,
            -0.42,
            7.1,
            11.417421,
            123,
            4,
            TimeSpan.FromMilliseconds(12),
            TimeSpan.FromMilliseconds(5),
            0.3,
            18500,
            true,
            16000,
            0.12,
            0.91,
            analyzedAt,
            null);

        await repository.UpsertAsync(expected, TestContext.Current.CancellationToken);
        var actual = await repository.GetAsync(_trackIdA, TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task CandidateQualityComparisonRepository_RoundTripsValues()
    {
        var repository = new SqliteCandidateQualityComparisonRepository(_database);
        var comparedAt = new DateTime(2026, 9, 12, 2, 3, 4, DateTimeKind.Utc);
        var expected = new CandidateQualityComparison(
            _trackIdA,
            _trackIdB,
            2,
            QualityAnalysisStatus.Analyzed,
            3.2,
            3.1,
            0.08,
            -1.4,
            -0.5,
            true,
            -0.18,
            comparedAt,
            null);

        await repository.UpsertAsync(expected, TestContext.Current.CancellationToken);
        var actual = await repository.GetAsync(_trackIdA, _trackIdB, TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task CandidateRemoval_CascadeDeletesQualityComparison()
    {
        var repository = new SqliteCandidateQualityComparisonRepository(_database);
        await repository.UpsertAsync(
            new CandidateQualityComparison(
                _trackIdA,
                _trackIdB,
                1,
                QualityAnalysisStatus.NotAnalyzed,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            TestContext.Current.CancellationToken);

        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync([], TestContext.Current.CancellationToken);

        var actual = await repository.GetAsync(_trackIdA, _trackIdB, TestContext.Current.CancellationToken);
        Assert.Null(actual);
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
