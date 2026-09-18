using Dapper;
using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Missing期間をまたぐHuman Verdict変更とKeep変更が、物理復帰時の安全条件を壊さないことを検証する。
/// </summary>
public sealed class DuplicateGroupRestoreInvariantTests : IAsyncLifetime
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
    public async Task VerdictClearedWhileTrackMissing_DoesNotReuseOldMissingWhenUserConfirmsAgain()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "clear-a.flac");
        var b = await CreateTrackAsync(tracks, "clear-b.flac");
        var c = await CreateTrackAsync(tracks, "clear-c.flac");
        var service = CreateService();
        var groups = new SqliteDuplicateGroupRepository(_database);

        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, b, c, b);
        var original = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetDerivedKeepStateAsync(_libraryId, original.Id, a, DuplicateGroupKeepStatus.Selected,
            "DerivedPreference", TestContext.Current.CancellationToken);

        await tracks.MarkMissingAsync(c, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        // Missing中にB-CのGlobal Verdictを明示的に解除する。このユーザー判断は古いTrackMissingより新しい正本である。
        await service.DeleteReviewAsync(
            _libraryId,
            CandidatePairKey.Create(b, c),
            TestContext.Current.CancellationToken);

        var restoredId = await tracks.UpsertMetadataAsync(CreateMetadata("clear-c.flac"), TestContext.Current.CancellationToken);
        Assert.Equal(c, restoredId);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restoredCountBeforeReconfirm = await GetTrackRestoredHistoryCountAsync();

        // Cが物理復帰した後でB-Cを再度重複と確定するのは、新しいHuman Verdict操作である。
        // 過去のTrackMissingをRestore理由として再利用し、明示KeepをUnselectedへ落としてはいけない。
        await SaveConfirmedAsync(service, b, c, b);

        var current = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Selected, current.KeepStatus);
        // A>BかつB>Cなので、再確定後の派生Keepは推移的優劣によりAとなる。
        Assert.Equal(a, current.KeepTrackId);
        Assert.Equal(restoredCountBeforeReconfirm, await GetTrackRestoredHistoryCountAsync());
    }

    [Fact]
    public async Task RestoreOldKeepAfterChoosingAlternativeWhileMissing_RequiresReview()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await CreateTrackAsync(tracks, "keep-a.flac");
        var b = await CreateTrackAsync(tracks, "keep-b.flac");
        var c = await CreateTrackAsync(tracks, "keep-c.flac");
        var service = CreateService();
        var groups = new SqliteDuplicateGroupRepository(_database);

        await SaveConfirmedAsync(service, a, b, a);
        await SaveConfirmedAsync(service, b, c, b);
        var original = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetDerivedKeepStateAsync(_libraryId, original.Id, a, DuplicateGroupKeepStatus.Selected,
            "DerivedPreference", TestContext.Current.CancellationToken);

        // 旧Keep=AをMissingへし、残ったB-Cに対して代替Keep=Bを選ぶ。
        // Keep変更はLibrary固有Dispositionだけなので、Aの物理復帰Guardを解除してはいけない。
        await tracks.MarkMissingAsync(a, TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);
        var reduced = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        await groups.SetDerivedKeepStateAsync(_libraryId, reduced.Id, b, DuplicateGroupKeepStatus.Selected,
            "DerivedPreference", TestContext.Current.CancellationToken);

        var restoredId = await tracks.UpsertMetadataAsync(CreateMetadata("keep-a.flac"), TestContext.Current.CancellationToken);
        Assert.Equal(a, restoredId);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), restored.GlobalTrackIds.Order().ToArray());
        Assert.Equal(DuplicateGroupKeepStatus.Selected, restored.KeepStatus);
        Assert.Equal(a, restored.KeepTrackId);
    }

    private DuplicateGroupService CreateService()
        => new(
            new SqliteCandidateReviewRepository(_database),
            new SqliteTrackLookupRepository(_database),
            new SqliteDuplicateGroupRepository(_database));

    private async Task SaveConfirmedAsync(
        DuplicateGroupService service,
        long left,
        long right,
        long keepTrackId)
    {
        await service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, keepTrackId, null),
            TestContext.Current.CancellationToken);
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

    private async Task<long> GetTrackRestoredHistoryCountAsync()
    {
        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory WHERE ChangeKind = 'TrackRestored';");
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
