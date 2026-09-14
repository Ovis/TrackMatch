using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Missing Trackの機械解析キャッシュは保持しつつ、Current処理とReportからは除外されることを検証する。
/// </summary>
public sealed class MissingMachineCurrentStateTests : IAsyncLifetime
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
    public async Task MissingTrack_HidesCachedComparisonAndClassificationUntilSameContentReturns()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "machine-a.flac");
        var b = await CreateTrackAsync(tracks, "machine-b.flac");
        var pair = CandidatePairKey.Create(a, b);

        await new SqliteCandidatePairRepository(_database, _libraryId).ReplaceAllAsync(
            [new CandidatePair(pair.TrackIdA, pair.TrackIdB, 0)],
            TestContext.Current.CancellationToken);

        var comparisons = new SqliteCandidateComparisonRepository(_database, _libraryId);
        await comparisons.UpsertAsync(
            [new CandidateComparison(
                pair.TrackIdA,
                pair.TrackIdB,
                0.99,
                0,
                TimeSpan.Zero,
                100,
                TimeSpan.FromSeconds(10),
                0.98,
                0.97,
                0.99)],
            TestContext.Current.CancellationToken);

        var classifications = new SqliteCandidateClassificationRepository(_database, _libraryId);
        await classifications.ReplaceAllAsync(
            [new CandidateClassification(
                pair.TrackIdA,
                pair.TrackIdB,
                AudioRelationshipKind.DuplicateCandidate,
                "test",
                "{}")],
            TestContext.Current.CancellationToken);

        Assert.Single(await comparisons.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Single(await comparisons.GetComparedAtUtcAsync(TestContext.Current.CancellationToken));
        Assert.Single(await classifications.GetReportAsync(TestContext.Current.CancellationToken));

        await tracks.MarkMissingAsync(b, TestContext.Current.CancellationToken);

        // MissingはIdentityや再利用可能なMachine Cacheを削除しないが、Current処理対象からは外す。
        Assert.Empty(await comparisons.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await comparisons.GetComparedAtUtcAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await classifications.GetReportAsync(TestContext.Current.CancellationToken));

        // Content Versionが変わらず同じPathへ戻った場合は保存済みMachine Cacheを再利用できる。
        var restoredId = await tracks.UpsertMetadataAsync(CreateMetadata("machine-b.flac"), TestContext.Current.CancellationToken);
        Assert.Equal(b, restoredId);
        Assert.Single(await comparisons.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Single(await comparisons.GetComparedAtUtcAsync(TestContext.Current.CancellationToken));
        Assert.Single(await classifications.GetReportAsync(TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateTrackAsync(SqliteTrackRepository repository, string fileName)
    {
        var trackId = await repository.UpsertMetadataAsync(CreateMetadata(fileName), TestContext.Current.CancellationToken);
        await repository.EnsureMembershipAsync(
            _libraryId,
            _rootId,
            trackId,
            fileName,
            TestContext.Current.CancellationToken);
        return trackId;
    }

    private AudioTrackMetadata CreateMetadata(string fileName)
        => new(
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
            2026);
}
