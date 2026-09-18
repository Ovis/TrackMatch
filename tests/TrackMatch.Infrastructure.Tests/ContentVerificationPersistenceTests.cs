using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Models;
using TrackMatch.Core.Duplicates;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Content Verification FailureとContent ChangedでHuman Verdictの扱いが分かれることを実SQLiteで検証する。
/// </summary>
public sealed class ContentVerificationPersistenceTests : IAsyncLifetime
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
            "Test Library",
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
    public async Task VerificationFailure_PreservesVerdictAndVerifiedRestoresUse()
    {
        var tracks = new SqliteTrackRepository(_database);
        var lookup = new SqliteTrackLookupRepository(_database);
        var a = await AddTrackAsync(tracks, "a.flac");
        var b = await AddTrackAsync(tracks, "b.flac");
        var pair = CandidatePairKey.Create(a, b);
        var reviews = new SqliteCandidateReviewRepository(_database, _libraryId);
        var groupService = new DuplicateGroupService(
            reviews,
            lookup,
            new SqliteDuplicateGroupRepository(_database));
        await groupService.SaveReviewAsync(
            _libraryId,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, a, null),
            TestContext.Current.CancellationToken);

        await tracks.MarkContentVerificationFailedAsync(a, TestContext.Current.CancellationToken);

        Assert.Single(await reviews.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.False(await lookup.IsHumanVerdictUsableAsync(a, TestContext.Current.CancellationToken));
        Assert.True(await lookup.IsFileOrganizationBlockedAsync(b, TestContext.Current.CancellationToken));

        await tracks.MarkContentVerifiedAsync(a, TestContext.Current.CancellationToken);

        Assert.Single(await reviews.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.True(await lookup.IsHumanVerdictUsableAsync(a, TestContext.Current.CancellationToken));
        Assert.False(await lookup.IsFileOrganizationBlockedAsync(b, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContentChanged_DeletesOnlyDirectVerdictsWithoutHistory()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await AddTrackAsync(tracks, "a.flac");
        var b = await AddTrackAsync(tracks, "b.flac");
        var c = await AddTrackAsync(tracks, "c.flac");
        var reviews = new SqliteCandidateReviewRepository(_database, _libraryId);
        await reviews.SaveAsync(
            new CandidateReview(CandidatePairKey.Create(a, b), CandidateReviewDecision.ConfirmedDuplicate, a, null),
            TestContext.Current.CancellationToken);
        await reviews.SaveAsync(
            new CandidateReview(CandidatePairKey.Create(b, c), CandidateReviewDecision.ConfirmedDuplicate, b, null),
            TestContext.Current.CancellationToken);

        var invalidated = await tracks.ConfirmContentChangedAndGetInvalidatedReviewCountAsync(
            a,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, invalidated);
        var remaining = Assert.Single(await reviews.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CandidatePairKey.Create(b, c), remaining.Pair);
        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM CandidateReviewHistory;",
                transaction: null));
    }

    private async Task<long> AddTrackAsync(SqliteTrackRepository tracks, string fileName)
    {
        var id = await tracks.UpsertMetadataAsync(
            new AudioTrackMetadata(
                Path.Combine(_directory, fileName),
                1024,
                new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMinutes(3),
                ["Artist"],
                fileName,
                "Album",
                1,
                1,
                ["J-POPS"]),
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
