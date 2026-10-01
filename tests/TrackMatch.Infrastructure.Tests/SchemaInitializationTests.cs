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
        var databasePath = Path.Combine(_directory, "unsupported-version.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaInfo (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL);
                INSERT INTO SchemaInfo (Id, Version) VALUES (1, 999);
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteDatabase(databasePath).InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("期待値: 2, 実際: 999", exception.Message, StringComparison.Ordinal);

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
        await verifyConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Libraries';";
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task InitializeAsync_MigratesVersion1ProjectionStatesAsClean()
    {
        var databasePath = Path.Combine(_directory, "version-1.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaInfo (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Version INTEGER NOT NULL);
                INSERT INTO SchemaInfo (Id, Version) VALUES (1, 1);

                CREATE TABLE Libraries (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    NormalizedName TEXT NOT NULL UNIQUE);
                INSERT INTO Libraries (Id, Name, NormalizedName) VALUES (42, 'Library', 'LIBRARY');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await new SqliteDatabase(databasePath).InitializeAsync(TestContext.Current.CancellationToken);

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
        await verifyConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT Version FROM SchemaInfo WHERE Id = 1),
                (SELECT GlobalGroupsDirty FROM ProjectionStates WHERE Id = 1),
                (SELECT KeepProjectionDirty FROM LibraryProjectionStates WHERE LibraryId = 42);
            """;
        await using var reader = await verifyCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }
}
