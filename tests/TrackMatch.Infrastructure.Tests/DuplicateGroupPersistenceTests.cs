using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Global Verdict更新とSQLite上のGlobal Duplicate Group、Library固有Keepが一貫することを検証する。
/// </summary>
public sealed class DuplicateGroupPersistenceTests : IAsyncLifetime
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
        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("Test Library", [_directory], TestContext.Current.CancellationToken);
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
    public async Task SaveReview_AddsThirdTrackAndChangesLibraryKeep()
    {
        var (a, b, c) = await CreateTracksAsync();
        var service = CreateService();

        await service.SaveReviewAsync(_libraryId, Confirmed(a, b, a), TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(_libraryId, Confirmed(b, c, b), TestContext.Current.CancellationToken);

        var group = Assert.Single(await new SqliteDuplicateGroupRepository(_database)
            .GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Selected, group.KeepStatus);
        Assert.Equal(b, group.KeepTrackId);
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), group.TrackIds);
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), group.GlobalTrackIds);
    }

    [Fact]
    public async Task DeleteReview_SplitsGroupAndLeavesRemainingProjectionWithValidKeep()
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
        Assert.Equal(new[] { a, b }.Order().ToArray(), group.GlobalTrackIds);
        Assert.Equal(DuplicateGroupKeepStatus.Selected, group.KeepStatus);
        Assert.NotNull(group.KeepTrackId);
        Assert.Contains(group.KeepTrackId.Value, group.TrackIds);
        Assert.DoesNotContain(c, group.GlobalTrackIds);
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
        var a = await CreateTrackAsync(tracks, "a.flac");
        var b = await CreateTrackAsync(tracks, "b.flac");
        var c = await CreateTrackAsync(tracks, "c.flac");
        return (a, b, c);
    }

    private async Task<long> CreateTrackAsync(SqliteTrackRepository tracks, string fileName)
    {
        var id = await tracks.UpsertMetadataAsync(CreateMetadata(fileName), TestContext.Current.CancellationToken);
        await tracks.EnsureMembershipAsync(
            _libraryId,
            _rootId,
            id,
            fileName,
            TestContext.Current.CancellationToken);
        return id;
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
