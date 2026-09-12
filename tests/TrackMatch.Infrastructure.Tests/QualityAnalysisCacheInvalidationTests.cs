using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Models;
using TrackMatch.Core.Quality;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Track実体の更新時だけ品質解析キャッシュが無効化されることを検証する。
/// </summary>
public sealed class QualityAnalysisCacheInvalidationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private SqliteTrackRepository _tracks = null!;
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

        _tracks = new SqliteTrackRepository(_database);
        _trackIdA = await _tracks.UpsertMetadataAsync(CreateMetadata("a.flac", 100, 1), TestContext.Current.CancellationToken);
        _trackIdB = await _tracks.UpsertMetadataAsync(CreateMetadata("b.flac", 100, 1), TestContext.Current.CancellationToken);
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
    public async Task UpdatingFileIdentity_InvalidatesTrackAndCandidateQualityCaches()
    {
        var trackQuality = new SqliteTrackQualityAnalysisRepository(_database);
        var candidateQuality = new SqliteCandidateQualityComparisonRepository(_database);
        await trackQuality.UpsertAsync(CreateTrackQuality(_trackIdA), TestContext.Current.CancellationToken);
        await trackQuality.UpsertAsync(CreateTrackQuality(_trackIdB), TestContext.Current.CancellationToken);
        await candidateQuality.UpsertAsync(
            new CandidateQualityComparison(
                _trackIdA,
                _trackIdB,
                1,
                QualityAnalysisStatus.Analyzed,
                1.2,
                1.2,
                0.1,
                -0.2,
                0.3,
                true,
                -0.1,
                DateTime.UtcNow,
                null),
            TestContext.Current.CancellationToken);

        var sameTrackId = await _tracks.UpsertMetadataAsync(
            CreateMetadata("a.flac", 120, 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(_trackIdA, sameTrackId);
        Assert.Null(await trackQuality.GetAsync(_trackIdA, TestContext.Current.CancellationToken));
        Assert.NotNull(await trackQuality.GetAsync(_trackIdB, TestContext.Current.CancellationToken));
        Assert.Null(await candidateQuality.GetAsync(_trackIdA, _trackIdB, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnchangedFileIdentity_PreservesTrackQualityCache()
    {
        var trackQuality = new SqliteTrackQualityAnalysisRepository(_database);
        await trackQuality.UpsertAsync(CreateTrackQuality(_trackIdA), TestContext.Current.CancellationToken);

        await _tracks.UpsertMetadataAsync(
            CreateMetadata("a.flac", 100, 1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(await trackQuality.GetAsync(_trackIdA, TestContext.Current.CancellationToken));
    }

    private TrackQualityAnalysis CreateTrackQuality(long trackId)
        => new(
            trackId,
            1,
            QualityAnalysisStatus.Analyzed,
            -12,
            -1,
            5,
            11,
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            18000,
            false,
            null,
            0.01,
            null,
            DateTime.UtcNow,
            null);

    private AudioTrackMetadata CreateMetadata(string fileName, long fileSize, int lastWriteDay)
        => new(
            Path.Combine(_directory, fileName),
            fileSize,
            new DateTime(2026, 9, lastWriteDay, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            "Title",
            "Album",
            1,
            1,
            ["J-POPS"]);
}
