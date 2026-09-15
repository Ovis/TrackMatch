using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class CandidateReviewPersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        await new SqliteLibraryRepository(_database).CreateAsync(
            "Test Library",
            [_directory],
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ReviewRepository_PersistsNotDuplicateReview()
    {
        var (trackIdA, trackIdB) = await CreateTracksAsync();
        var repository = new SqliteCandidateReviewRepository(_database);
        var pair = CandidatePairKey.Create(trackIdB, trackIdA);

        await repository.SaveAsync(
            new CandidateReview(pair, CandidateReviewDecision.NotDuplicate, "別アレンジ"),
            TestContext.Current.CancellationToken);

        var reviews = await repository.GetAllAsync(TestContext.Current.CancellationToken);
        var review = Assert.Single(reviews);
        Assert.Equal(CandidateReviewDecision.NotDuplicate, review.Decision);
        Assert.Equal("別アレンジ", review.Note);
        Assert.Contains(CandidatePairKey.Create(trackIdA, trackIdB),
            await repository.GetExcludedPairKeysAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReviewRepository_PersistsConfirmedDuplicateWithoutPairLevelKeep()
    {
        var (trackIdA, trackIdB) = await CreateTracksAsync();
        var repository = new SqliteCandidateReviewRepository(_database);
        var pair = CandidatePairKey.Create(trackIdA, trackIdB);

        await repository.SaveAsync(
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, "重複として確認"),
            TestContext.Current.CancellationToken);

        var review = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CandidateReviewDecision.ConfirmedDuplicate, review.Decision);
        Assert.Equal("重複として確認", review.Note);
        Assert.Contains(pair, await repository.GetExcludedPairKeysAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReviewRepository_ReplacingDecisionArchivesPreviousGlobalVerdict()
    {
        var (trackIdA, trackIdB) = await CreateTracksAsync();
        var repository = new SqliteCandidateReviewRepository(_database);
        var pair = CandidatePairKey.Create(trackIdA, trackIdB);
        await repository.SaveAsync(
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, "最初の判定"),
            TestContext.Current.CancellationToken);

        await repository.SaveAsync(
            new CandidateReview(pair, CandidateReviewDecision.NotDuplicate, "再確認"),
            TestContext.Current.CancellationToken);

        var review = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CandidateReviewDecision.NotDuplicate, review.Decision);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Decision, ChangeKind FROM CandidateReviewHistory WHERE TrackIdA = $a AND TrackIdB = $b;";
        command.Parameters.AddWithValue("$a", pair.TrackIdA);
        command.Parameters.AddWithValue("$b", pair.TrackIdB);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(nameof(CandidateReviewDecision.ConfirmedDuplicate), reader.GetString(0));
        Assert.Equal("UserChanged", reader.GetString(1));
    }

    private async Task<(long TrackIdA, long TrackIdB)> CreateTracksAsync()
    {
        var tracks = new SqliteTrackRepository(_database);
        var trackIdA = await tracks.UpsertMetadataAsync(CreateMetadata("a.flac"), TestContext.Current.CancellationToken);
        var trackIdB = await tracks.UpsertMetadataAsync(CreateMetadata("b.flac"), TestContext.Current.CancellationToken);
        return (trackIdA, trackIdB);
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
