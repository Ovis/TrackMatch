using Dapper;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class HumanVerdictSchemaTests
{
    [Fact]
    public async Task InitializeAsync_CreatesPreferredTrackColumnAndSchemaVersion3()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trackmatch-schema-{Guid.NewGuid():N}.db");
        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            await using var connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            var version = await connection.ExecuteScalarAsync<long>("SELECT Version FROM SchemaInfo WHERE Id = 1;");
            var columns = (await connection.QueryAsync<ColumnRow>("PRAGMA table_info(CandidateReviews);")).ToArray();
            Assert.Equal(3, version);
            Assert.Contains(columns, column => column.Name == "PreferredTrackId");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed record ColumnRow(string Name);
}
