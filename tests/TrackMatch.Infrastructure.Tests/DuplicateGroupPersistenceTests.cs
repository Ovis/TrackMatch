using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// レビュー更新とSQLite上の重複グループ再構成が一貫することを検証する。
/// </summary>
public sealed class DuplicateGroupPersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private long _libraryId;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        var libraries = new SqliteLibraryRepository(_database);
        await libraries.CreateAsync("Test Library", [_directory], TestContext.Current.CancellationToken);
        _libraryId = Assert.Single(await libraries.GetAllAsync(TestContext.Current.CancellationToken)).Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveReview_AddsThirdTrackAndChangesGroupKeep()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();

        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        var group = Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(b, group.KeepTrackId);
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), group.TrackIds);
    }

    [Fact]
    public async Task DeleteReview_SplitsGroupAndLeavesEveryRemainingGroupWithKeep()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        await service.DeleteReviewAsync(
            _libraryId,
            CandidatePairKey.Create(b, c),
            TestContext.Current.CancellationToken);

        var group = Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b }.Order().ToArray(), group.TrackIds);
        Assert.Contains(group.KeepTrackId, group.TrackIds);
        Assert.DoesNotContain(c, group.TrackIds);
    }

    [Fact]
    public async Task SaveReview_ContradictingNotDuplicateIsRejectedBeforePersistence()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();
        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(a, c), CandidateReviewDecision.NotDuplicate, null),
            TestContext.Current.CancellationToken));

        var reviews = await new SqliteCandidateReviewRepository(_database)
            .GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, reviews.Count);
        Assert.DoesNotContain(reviews, review => review.Pair == CandidatePairKey.Create(a, c));
    }

    private DuplicateGroupService CreateService()
    {
        var reviews = new SqliteCandidateReviewRepository(_database);
        var tracks = new SqliteTrackLookupRepository(_database);
        return new DuplicateGroupService(reviews, tracks, new SqliteDuplicateGroupRepository(_database));
    }

    private async Task<(long A, long B, long C)> CreateTracksAsync()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await tracks.UpsertMetadataAsync(CreateMetadata("a.flac"), TestContext.Current.CancellationToken);
        var b = await tracks.UpsertMetadataAsync(CreateMetadata("b.flac"), TestContext.Current.CancellationToken);
        var c = await tracks.UpsertMetadataAsync(CreateMetadata("c.flac"), TestContext.Current.CancellationToken);
        return (a, b, c);
    }

    private AudioTrackMetadata CreateMetadata(string fileName)
        => new(
            Path.Combine(_directory, fileName),
            1024,
            new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(3),
            ["Artist"],
            "Title",
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
            2026);

    private static CandidateReview Confirmed(long left, long right, long keep)
        => new(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, null, keep);
}
