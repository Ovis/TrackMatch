using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

public sealed class ProjectionStatePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TrackMatch.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabase _database;

    public ProjectionStatePersistenceTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(Path.Combine(_directory, "projection-state.db"));
        _database.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task MarkReviewMutationStartedAsync_MarksGlobalAndCurrentLibraryDirty()
    {
        var library = await new SqliteLibraryRepository(_database).CreateAsync(
            "Library",
            [_directory],
            TestContext.Current.CancellationToken);
        var states = new SqliteProjectionStateRepository(_database);

        await states.MarkReviewMutationStartedAsync(library.Id, TestContext.Current.CancellationToken);

        Assert.True(await states.IsGlobalGroupsDirtyAsync(TestContext.Current.CancellationToken));
        Assert.True(await states.IsLibraryKeepProjectionDirtyAsync(library.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DirtyStates_CanBeUpdatedIndependently()
    {
        var libraries = new SqliteLibraryRepository(_database);
        var first = await libraries.CreateAsync("First", [_directory], TestContext.Current.CancellationToken);
        var secondRoot = Path.Combine(_directory, "Second");
        Directory.CreateDirectory(secondRoot);
        var second = await libraries.CreateAsync("Second", [secondRoot], TestContext.Current.CancellationToken);
        var states = new SqliteProjectionStateRepository(_database);
        await states.MarkReviewMutationStartedAsync(first.Id, TestContext.Current.CancellationToken);

        await states.SetLibraryKeepProjectionDirtyAsync(second.Id, true, TestContext.Current.CancellationToken);
        await states.SetLibraryKeepProjectionDirtyAsync(first.Id, false, TestContext.Current.CancellationToken);
        await states.SetGlobalGroupsDirtyAsync(false, TestContext.Current.CancellationToken);

        Assert.False(await states.IsGlobalGroupsDirtyAsync(TestContext.Current.CancellationToken));
        Assert.False(await states.IsLibraryKeepProjectionDirtyAsync(first.Id, TestContext.Current.CancellationToken));
        Assert.True(await states.IsLibraryKeepProjectionDirtyAsync(second.Id, TestContext.Current.CancellationToken));
    }
}
