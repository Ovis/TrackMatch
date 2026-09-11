using Microsoft.Data.Sqlite;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// LibraryとRootの永続化規則を実SQLiteで検証する。
/// </summary>
public sealed class LibraryPersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;
    private SqliteLibraryRepository _repository = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "trackmatch.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        _repository = new SqliteLibraryRepository(_database);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateAsync_NormalizesNameAndCreatesRootsAtomically()
    {
        var library = await _repository.CreateAsync(
            "  My   Music  ",
            [@"d:/Music/./Jp", @"E:\\Imported\\..\\ImportedMusic\\"],
            TestContext.Current.CancellationToken);

        Assert.Equal("My Music", library.Name);
        Assert.Equal([@"D:\Music\Jp", @"E:\ImportedMusic"], library.Roots.Select(root => root.Path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CreateAsync(
            "my music",
            [@"F:\Other"],
            TestContext.Current.CancellationToken));

        var libraries = await _repository.GetAllAsync(TestContext.Current.CancellationToken);
        var stored = Assert.Single(libraries);
        Assert.Equal(2, stored.Roots.Count);
    }

    [Fact]
    public async Task CreateAsync_RejectsOverlappingRootsWithoutLeavingPartialLibrary()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.CreateAsync(
            "Music",
            [@"D:\Music", @"d:\music\Anime"],
            TestContext.Current.CancellationToken));

        Assert.Empty(await _repository.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddRootAsync_RejectsOverlapAcrossLibraries()
    {
        var music = await _repository.CreateAsync(
            "Music",
            [@"D:\Music"],
            TestContext.Current.CancellationToken);
        await _repository.CreateAsync(
            "Soundtrack",
            [@"E:\Soundtrack"],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.AddRootAsync(
            music.Id,
            @"E:\Soundtrack\Anime",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveRootAsync_RejectsLastRoot()
    {
        var library = await _repository.CreateAsync(
            "Music",
            [@"D:\Music"],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.RemoveRootAsync(
            library.Id,
            Assert.Single(library.Roots).Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveRootAsync_AllowsRemovalAfterAddingReplacement()
    {
        var library = await _repository.CreateAsync(
            "Music",
            [@"D:\Music"],
            TestContext.Current.CancellationToken);
        var replacement = await _repository.AddRootAsync(
            library.Id,
            @"E:\Music",
            TestContext.Current.CancellationToken);

        await _repository.RemoveRootAsync(
            library.Id,
            Assert.Single(library.Roots).Id,
            TestContext.Current.CancellationToken);

        var stored = Assert.IsType<TrackMatch.Core.Libraries.Library>(
            await _repository.GetAsync(library.Id, TestContext.Current.CancellationToken));
        var root = Assert.Single(stored.Roots);
        Assert.Equal(replacement.Id, root.Id);
    }

    [Fact]
    public async Task DeleteAsync_DeletesLibraryAndRoots()
    {
        var library = await _repository.CreateAsync(
            "Music",
            [@"D:\Music", @"E:\Imported"],
            TestContext.Current.CancellationToken);

        await _repository.DeleteAsync(library.Id, TestContext.Current.CancellationToken);

        Assert.Null(await _repository.GetAsync(library.Id, TestContext.Current.CancellationToken));
        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM LibraryRoots WHERE LibraryId = $libraryId;";
        command.Parameters.AddWithValue("$libraryId", library.Id);
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
