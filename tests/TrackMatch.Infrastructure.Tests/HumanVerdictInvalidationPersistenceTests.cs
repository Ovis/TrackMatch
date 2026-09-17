using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class HumanVerdictInvalidationPersistenceTests
{
    [Fact]
    public async Task InvalidateByTrackAsync_MovesCurrentVerdictToHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trackmatch-invalidation-{Guid.NewGuid():N}.db");
        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            await using (var connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                await connection.ExecuteAsync("INSERT INTO Tracks (Id,Path,PathKey,FileSize,LastWriteTimeUtcTicks,DurationTicks,ArtistsJson,GenresJson,IsMissing,UpdatedAtUtcTicks) VALUES (1,'a','a',1,1,1,'[]','[]',0,1),(2,'b','b',1,1,1,'[]','[]',0,1);");
                await connection.ExecuteAsync("INSERT INTO CandidateReviews (TrackIdA,TrackIdB,Decision,PreferredTrackId,ReviewedAtUtcTicks) VALUES (1,2,'ConfirmedDuplicate',1,1);");
            }

            await new SqliteHumanVerdictInvalidationRepository(database).InvalidateByTrackAsync(1, HumanVerdictInvalidationReason.ContentChanged, TestContext.Current.CancellationToken);

            await using var verify = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CandidateReviews;"));
            Assert.Equal("ContentChanged", await verify.ExecuteScalarAsync<string>("SELECT InvalidationReason FROM CandidateReviewHistory LIMIT 1;"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
