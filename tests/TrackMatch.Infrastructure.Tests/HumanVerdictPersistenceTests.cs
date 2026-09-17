using TrackMatch.Core.Candidates;
using TrackMatch.Core.Libraries;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>Human VerdictのPreferred TrackをCurrent Stateへ保存できることを検証する。</summary>
public sealed class HumanVerdictPersistenceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"trackmatch-human-verdict-{Guid.NewGuid():N}.db");
    private SqliteDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new SqliteDatabase(_databasePath);
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_RoundTripsPreferredTrack()
    {
        var libraries = new SqliteLibraryRepository(_database);
        var library = await libraries.CreateAsync("test", [Path.GetDirectoryName(_databasePath)!], TestContext.Current.CancellationToken);
        var tracks = new SqliteTrackRepository(_database);
        var a = await tracks.UpsertAsync(library.Id, library.Roots[0].Id, Track("a.flac"), TestContext.Current.CancellationToken);
        var b = await tracks.UpsertAsync(library.Id, library.Roots[0].Id, Track("b.flac"), TestContext.Current.CancellationToken);
        var repository = new SqliteCandidateReviewRepository(_database, library.Id);

        await repository.SaveAsync(CandidateReviewFactory.ConfirmedDuplicate(a.TrackId, b.TrackId, b.TrackId), TestContext.Current.CancellationToken);

        var review = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(b.TrackId, review.PreferredTrackId);
    }

    private static ScannedTrack Track(string fileName)
        => new(fileName, 100, DateTime.UnixEpoch, TimeSpan.FromMinutes(3), [], fileName, null, null, null, [], null, "flac", "flac", 1000, 44100, 16, 2);
}
