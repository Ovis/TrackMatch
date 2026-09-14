using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Candidate Review一覧へGlobal Verdictの出所と再確認推奨が投影されることを実SQLiteで検証する。
/// </summary>
public sealed class CandidateReviewReportPersistenceTests : IAsyncLifetime
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
            "Source Library",
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
    public async Task GetAsync_ReturnsReviewSourceAndReReviewRecommendationForStrongContradiction()
    {
        var tracks = new SqliteTrackRepository(_database);
        var trackA = await AddTrackAsync(tracks, "a.flac");
        var trackB = await AddTrackAsync(tracks, "b.flac");
        await new SqliteCandidatePairRepository(_database).ReplaceAllAsync(
            [new CandidatePair(trackA, trackB, 0)],
            TestContext.Current.CancellationToken);
        await new SqliteCandidateComparisonRepository(_database).ReplaceAllAsync(
            [new CandidateComparison(
                trackA,
                trackB,
                0.99,
                0,
                TimeSpan.Zero,
                100,
                TimeSpan.FromMinutes(3),
                0.99,
                0.99,
                1.0)],
            TestContext.Current.CancellationToken);

        await using (var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO CandidateClassifications (
                    TrackIdA, TrackIdB, Kind, Reason, ThresholdProfileJson, ClassifiedAtUtcTicks)
                VALUES (@TrackIdA, @TrackIdB, 'DuplicateCandidate', 'test', '{}', @Ticks);
                """,
                new { TrackIdA = trackA, TrackIdB = trackB, Ticks = DateTime.UtcNow.Ticks });
        }

        await new SqliteCandidateReviewRepository(_database, _libraryId).SaveAsync(
            new CandidateReview(
                CandidatePairKey.Create(trackA, trackB),
                CandidateReviewDecision.NotDuplicate,
                null),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(await new SqliteCandidateReviewReportRepository(_database)
            .GetAsync(_libraryId, TestContext.Current.CancellationToken));

        Assert.Equal(_libraryId, row.ReviewSourceLibraryId);
        Assert.Equal("Source Library", row.ReviewSourceLibraryName);
        Assert.True(row.ReReviewRecommended);
    }

    private async Task<long> AddTrackAsync(SqliteTrackRepository tracks, string fileName)
    {
        var id = await tracks.UpsertMetadataAsync(
            new AudioTrackMetadata(
                Path.Combine(_directory, fileName),
                1024,
                new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMinutes(3),
                ["Artist"],
                fileName,
                "Album",
                1,
                1,
                ["J-POPS"],
                "FLAC",
                "FLAC",
                900,
                44100,
                16,
                2,
                2026),
            TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(
            _libraryId,
            _rootId,
            id,
            fileName,
            TestContext.Current.CancellationToken);
        return id;
    }
}
