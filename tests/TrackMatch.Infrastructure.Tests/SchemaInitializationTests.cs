using Microsoft.Data.Sqlite;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// TrackMatch DB Schemaの初期化とVersion拒否条件を実SQLiteで検証する。
/// </summary>
public sealed class SchemaInitializationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));

    public SchemaInitializationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task InitializeAsync_RejectsExistingUnversionedSchemaWithoutAddingSchemaInfo()
    {
        var databasePath = Path.Combine(_directory, "legacy.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Tracks (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteDatabase(databasePath).InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Version管理前", exception.Message, StringComparison.Ordinal);

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
        await verifyConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaInfo';";
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task InitializeAsync_RejectsUnsupportedVersionBeforeCreatingCurrentSchemaTables()
    {
        var databasePath = Path.Combine(_directory, "version1.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaInfo (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL);
                INSERT INTO SchemaInfo (Id, Version) VALUES (1, 1);
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteDatabase(databasePath).InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("期待値: 6, 実際: 1", exception.Message, StringComparison.Ordinal);

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
        await verifyConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Libraries';";
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
