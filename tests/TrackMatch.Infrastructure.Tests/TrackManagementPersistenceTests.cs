using Microsoft.Data.Sqlite;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Management;
using TrackMatch.Core.Models;
using TrackMatch.Infrastructure.Persistence;
using Xunit;

namespace TrackMatch.Infrastructure.Tests;

/// <summary>
/// Global Track管理画面で使用するFilter、Force Reanalysis、管理データ削除を実SQLiteで検証する。
/// </summary>
public sealed class TrackManagementPersistenceTests : IAsyncLifetime
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
            "Music",
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
    public async Task GetTracksAsync_FiltersMissingAndUnownedIndependently()
    {
        var tracks = new SqliteTrackRepository(_database);
        var availableId = await AddTrackAsync(tracks, "available.flac", addMembership: true);
        var missingId = await AddTrackAsync(tracks, "missing.flac", addMembership: true);
        var unownedId = await AddTrackAsync(tracks, "unowned.flac", addMembership: false);
        await tracks.MarkMissingAsync(missingId, TestContext.Current.CancellationToken);
        var repository = new SqliteTrackManagementRepository(_database);

        var all = await repository.GetTracksAsync(TrackManagementFilter.All, TestContext.Current.CancellationToken);
        var missing = await repository.GetTracksAsync(TrackManagementFilter.Missing, TestContext.Current.CancellationToken);
        var unowned = await repository.GetTracksAsync(TrackManagementFilter.Unowned, TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
        Assert.Equal(missingId, Assert.Single(missing).TrackId);
        Assert.Equal(unownedId, Assert.Single(unowned).TrackId);
        Assert.False(all.Single(track => track.TrackId == availableId).IsMissing);
        Assert.Equal(["Music"], all.Single(track => track.TrackId == availableId).LibraryNames);
    }

    [Fact]
    public async Task ForceReanalysisTrackAsync_InvalidatesMachineStateAndArchivesCurrentReview()
    {
        var tracks = new SqliteTrackRepository(_database);
        var trackA = await AddTrackAsync(tracks, "a.flac", addMembership: true);
        var trackB = await AddTrackAsync(tracks, "b.flac", addMembership: true);
        await tracks.SaveFingerprintAsync(trackA, Fingerprint("a.flac"), 2, TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(trackB, Fingerprint("b.flac"), 2, TestContext.Current.CancellationToken);
        var reviews = new SqliteCandidateReviewRepository(_database, _libraryId);
        await reviews.SaveAsync(
            new CandidateReview(
                CandidatePairKey.Create(trackA, trackB),
                CandidateReviewDecision.ConfirmedDuplicate,
                "confirmed"),
            TestContext.Current.CancellationToken);
        var repository = new SqliteTrackManagementRepository(_database);

        var result = await repository.ForceReanalysisTrackAsync(trackA, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TrackCount);
        Assert.Equal(1, result.ArchivedReviewCount);
        Assert.Null(await tracks.GetFingerprintAsync(trackA, TestContext.Current.CancellationToken));
        Assert.NotNull(await tracks.GetFingerprintAsync(trackB, TestContext.Current.CancellationToken));
        Assert.Empty(await reviews.GetAllAsync(TestContext.Current.CancellationToken));

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM CandidateReviewHistory WHERE TrackIdA = $id OR TrackIdB = $id;",
            trackA));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT CandidateGenerationPending FROM LibraryTracks WHERE LibraryId = $libraryId AND TrackId = $id;",
            trackA,
            _libraryId));
    }

    [Fact]
    public async Task ForceReanalysisTrackAsync_PreservesDeletedSourceLibraryNameInReviewHistory()
    {
        var tracks = new SqliteTrackRepository(_database);
        var trackA = await AddTrackAsync(tracks, "source-a.flac", addMembership: true);
        var trackB = await AddTrackAsync(tracks, "source-b.flac", addMembership: true);
        var pair = CandidatePairKey.Create(trackA, trackB);
        await new SqliteCandidateReviewRepository(_database, _libraryId).SaveAsync(
            new CandidateReview(pair, CandidateReviewDecision.NotDuplicate, null),
            TestContext.Current.CancellationToken);

        await new SqliteLibraryRepository(_database).DeleteAsync(_libraryId, TestContext.Current.CancellationToken);

        await new SqliteTrackManagementRepository(_database)
            .ForceReanalysisTrackAsync(trackA, TestContext.Current.CancellationToken);

        await using var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SourceLibraryNameSnapshot
            FROM CandidateReviewHistory
            WHERE TrackIdA = $trackIdA AND TrackIdB = $trackIdB
              AND ChangeKind = 'ForceReanalysis'
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$trackIdA", pair.TrackIdA);
        command.Parameters.AddWithValue("$trackIdB", pair.TrackIdB);
        var sourceName = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Music", sourceName);
    }

    [Fact]
    public async Task DeleteTracksAsync_RemovesTrackMatchDataWithoutDeletingAudioFile()
    {
        var path = Path.Combine(_directory, "unowned.flac");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var tracks = new SqliteTrackRepository(_database);
        var trackId = await tracks.UpsertMetadataAsync(CreateMetadata(path), TestContext.Current.CancellationToken);
        await tracks.SaveFingerprintAsync(trackId, Fingerprint(path), 2, TestContext.Current.CancellationToken);
        var repository = new SqliteTrackManagementRepository(_database);

        var deleted = await repository.DeleteTracksAsync([trackId], TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Null(await tracks.GetByPathAsync(path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteTracksAsync_RemovesSelectedKeepBeforeDeletedTrackFkCanNullIt()
    {
        var tracks = new SqliteTrackRepository(_database);
        var a = await AddTrackAsync(tracks, "a.flac", addMembership: true);
        var b = await AddTrackAsync(tracks, "b.flac", addMembership: true);
        var c = await AddTrackAsync(tracks, "c.flac", addMembership: true);
        var groups = new SqliteDuplicateGroupRepository(_database);
        var service = new DuplicateGroupService(
            new SqliteCandidateReviewRepository(_database),
            new SqliteTrackLookupRepository(_database),
            groups);

        // TriangleにしておくことでKeep=Aを完全削除してもB-CのGlobal VerdictとGroup自体は残る。
        await service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(a, b), CandidateReviewDecision.ConfirmedDuplicate, null),
            a,
            TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(b, c), CandidateReviewDecision.ConfirmedDuplicate, null),
            b,
            TestContext.Current.CancellationToken);
        await service.SaveReviewAsync(
            _libraryId,
            new CandidateReview(CandidatePairKey.Create(a, c), CandidateReviewDecision.ConfirmedDuplicate, null),
            a,
            TestContext.Current.CancellationToken);

        var before = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupKeepStatus.Selected, before.KeepStatus);
        Assert.Equal(a, before.KeepTrackId);

        await new SqliteTrackManagementRepository(_database)
            .DeleteTracksAsync([a], TestContext.Current.CancellationToken);
        await service.SynchronizeGlobalAsync(TestContext.Current.CancellationToken);

        var after = Assert.Single(await groups.GetByLibraryIdAsync(_libraryId, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { b, c }.Order().ToArray(), after.GlobalTrackIds.Order().ToArray());
        Assert.Equal(DuplicateGroupKeepStatus.Unselected, after.KeepStatus);
        Assert.Null(after.KeepTrackId);
    }

    [Fact]
    public async Task DeleteTracksAsync_RemovesKeepHistoryWhenTrackExistsOnlyInGraphSnapshot()
    {
        var tracks = new SqliteTrackRepository(_database);
        var keepTrackId = await AddTrackAsync(tracks, "keep.flac", addMembership: true);
        var deletedTrackId = await AddTrackAsync(tracks, "delete.flac", addMembership: true);

        await using (var connection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO LibraryDuplicateGroupKeepHistory (
                    LibraryId, LibraryNameSnapshot, DuplicateGroupId, GraphKeySnapshot,
                    KeepTrackId, Status, ChangeKind, ChangedAtUtcTicks, Note)
                VALUES ($libraryId, 'Music', NULL, $graphKey, $keepTrackId, 'Selected', 'Test', $ticks, NULL);
                """;
            command.Parameters.AddWithValue("$libraryId", _libraryId);
            command.Parameters.AddWithValue("$graphKey", $"{Math.Min(keepTrackId, deletedTrackId)},{Math.Max(keepTrackId, deletedTrackId)}");
            command.Parameters.AddWithValue("$keepTrackId", keepTrackId);
            command.Parameters.AddWithValue("$ticks", DateTime.UtcNow.Ticks);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await new SqliteTrackManagementRepository(_database)
            .DeleteTracksAsync([deletedTrackId], TestContext.Current.CancellationToken);

        await using var verifyConnection = await _database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM LibraryDuplicateGroupKeepHistory;";
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    private async Task<long> AddTrackAsync(
        SqliteTrackRepository tracks,
        string fileName,
        bool addMembership)
    {
        var path = Path.Combine(_directory, fileName);
        var id = await tracks.UpsertMetadataAsync(CreateMetadata(path), TestContext.Current.CancellationToken);
        if (addMembership)
        {
            await tracks.EnsureMembershipAsync(
                _libraryId,
                _rootId,
                id,
                fileName,
                TestContext.Current.CancellationToken);
        }

        return id;
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            100,
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            Path.GetFileNameWithoutExtension(path),
            "Album",
            1,
            1,
            ["J-POPS"]);

    private static AudioFingerprint Fingerprint(string path)
        => new(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u]);

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        long id,
        long? libraryId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        if (libraryId is not null)
        {
            command.Parameters.AddWithValue("$libraryId", libraryId.Value);
        }

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
