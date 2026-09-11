using Microsoft.Data.Sqlite;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class SqlitePersistenceTests : IAsyncLifetime
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
    public async Task InitializeAsync_UsesWalJournalMode()
    {
        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        var mode = Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task TrackRepository_UpsertsMetadataAndRoundTripsFingerprint()
    {
        var repository = new SqliteTrackRepository(_database);
        var path = Path.Combine(_directory, "テスト.flac");
        var initial = CreateMetadata(path, 100, "Old title");
        var id = await repository.UpsertMetadataAsync(initial, TestContext.Current.CancellationToken);

        var updated = CreateMetadata(path, 200, "New title");
        var updatedId = await repository.UpsertMetadataAsync(updated, TestContext.Current.CancellationToken);

        Assert.Equal(id, updatedId);
        var stored = Assert.IsType<StoredTrack>(await repository.GetByPathAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(200, stored.Metadata.FileSize);
        Assert.Equal("New title", stored.Metadata.Title);
        Assert.Equal(["Artist A", "Artist B"], stored.Metadata.Artists);
        Assert.Equal(["J-POPS"], stored.Metadata.Genres);
        Assert.Equal(_libraryId, stored.LibraryId);
        Assert.Equal(_rootId, stored.RootId);
        Assert.Equal("テスト.flac", stored.RelativePath);

        var fingerprint = new AudioFingerprint(path, updated.Duration, [0u, 1u, uint.MaxValue, 0x12345678u]);
        await repository.SaveFingerprintAsync(id, fingerprint, algorithm: 2, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(id, await repository.GetTrackIdsWithoutFingerprintByRootPathAsync(_directory, TestContext.Current.CancellationToken));

        var restored = Assert.IsType<AudioFingerprint>(
            await repository.GetFingerprintAsync(id, TestContext.Current.CancellationToken));
        Assert.Equal(fingerprint.Path, restored.Path);
        Assert.Equal(fingerprint.Duration, restored.Duration);
        Assert.Equal(fingerprint.Values, restored.Values);

        await repository.DeleteFingerprintAsync(id, TestContext.Current.CancellationToken);
        Assert.Null(await repository.GetFingerprintAsync(id, TestContext.Current.CancellationToken));
        Assert.Contains(id, await repository.GetTrackIdsWithoutFingerprintByRootPathAsync(_directory, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrackRepository_GetsRootTracksAndMarksMissing()
    {
        var repository = new SqliteTrackRepository(_database);
        var path = Path.Combine(_directory, "Album", "track.flac");
        var id = await repository.UpsertMetadataAsync(CreateMetadata(path, 100, "Track"), TestContext.Current.CancellationToken);

        var tracks = await repository.GetByRootPathAsync(_directory, TestContext.Current.CancellationToken);
        var track = Assert.Single(tracks);
        Assert.Equal(id, track.Id);
        Assert.Equal(Path.Combine("Album", "track.flac"), track.RelativePath);

        await repository.MarkMissingAsync(id, TestContext.Current.CancellationToken);
        var missing = Assert.IsType<StoredTrack>(await repository.GetByPathAsync(path, TestContext.Current.CancellationToken));
        Assert.True(missing.IsMissing);
    }

    [Fact]
    public async Task TrackRepository_RejectsTrackOutsideRegisteredRoots()
    {
        var repository = new SqliteTrackRepository(_database);
        var outsidePath = Path.Combine(Path.GetTempPath(), "TrackMatch.Outside", Guid.NewGuid().ToString("N"), "other.flac");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertMetadataAsync(
            CreateMetadata(outsidePath, 100, "Other"),
            TestContext.Current.CancellationToken));

        Assert.Contains("Library Root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanSessionRepository_CompletesRunningSession()
    {
        var repository = new SqliteScanSessionRepository(_database);
        var id = await repository.StartAsync(_directory, DateTime.UtcNow, TestContext.Current.CancellationToken);
        var summary = new ScanSessionSummary(100, 98, 10, 5, 2, 2);

        await repository.CompleteAsync(id, DateTime.UtcNow, summary, TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, TotalFiles, ErrorCount FROM ScanSessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Completed", reader.GetString(0));
        Assert.Equal(100, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
    }

    private static AudioTrackMetadata CreateMetadata(string path, long size, string title)
        => new(
            path,
            size,
            new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist A", "Artist B"],
            title,
            "Album",
            1,
            1,
            ["J-POPS"]);
}
