using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Trash;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Library外ファイル向けの明示Global TrashがLibrary内ファイルや現在Keepへ誤用されないことを検証する。
/// </summary>
public sealed class ExplicitGlobalTrashScopeTests
{
    [Fact]
    public async Task ProcessTrackGloballyAsync_CurrentLibraryTrackIsRejectedBeforePhysicalMove()
    {
        var source = Path.Combine(Path.GetTempPath(), "TrackMatch", "current-library.flac");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var tracks = new FakeTrackRepository(CreateTrack(1, source), libraryId: 10);
        var files = new FakeFileOperations(source);
        var service = new RejectedTrackTrashService(new FakeGroupRepository(), tracks, tracks, files);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessTrackGloballyAsync(
            10,
            1,
            trash,
            execute: true,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("現在のライブラリ", exception.Message, StringComparison.Ordinal);
        Assert.Empty(files.Moves);
        Assert.Empty(tracks.MarkedMissing);
    }

    [Fact]
    public async Task ProcessTrackGloballyAsync_ExternalCurrentKeepIsRejectedBeforePhysicalMove()
    {
        var source = Path.Combine(Path.GetTempPath(), "TrackMatch", "external-keep.flac");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var tracks = new FakeTrackRepository(CreateTrack(1, source), libraryId: 20);
        var files = new FakeFileOperations(source);
        var currentProjection = new DuplicateGroup(
            5,
            10,
            1,
            DuplicateGroupKeepStatus.Selected,
            [2],
            [1, 2]);
        var service = new RejectedTrackTrashService(
            new FakeGroupRepository(currentProjection),
            tracks,
            tracks,
            files);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessTrackGloballyAsync(
            10,
            1,
            trash,
            execute: true,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("残すファイル", exception.Message, StringComparison.Ordinal);
        Assert.Empty(files.Moves);
        Assert.Empty(tracks.MarkedMissing);
    }

    private static StoredTrack CreateTrack(long id, string path)
        => new(
            id,
            new AudioTrackMetadata(
                path,
                100,
                new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMinutes(4),
                ["Artist"],
                "Title",
                "Album",
                1,
                1,
                ["J-POPS"]),
            IsMissing: false);

    private sealed class FakeGroupRepository(DuplicateGroup? group = null) : IDuplicateGroupRepository
    {
        public Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GlobalDuplicateGroup>>([]);

        public Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DuplicateGroup>>(
                group is not null && group.LibraryId == libraryId ? [group] : []);

        public Task<DuplicateGroup?> GetByTrackIdAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                group is not null
                && group.LibraryId == libraryId
                && group.GlobalTrackIds.Contains(trackId)
                    ? group
                    : null);

        public Task<DuplicateGroup?> GetByIdAsync(long groupId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                group is not null && group.Id == groupId && group.LibraryId == libraryId ? group : null);

        public Task ReplaceGlobalAsync(IReadOnlyCollection<DuplicateGroupRebuildItem> groups, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetKeepAsync(long libraryId, long groupId, long keepTrackId, string changeKind, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeTrackRepository(StoredTrack track, long libraryId) : ITrackRepository, ITrackLookupRepository
    {
        public List<long> MarkedMissing { get; } = [];

        public Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredTrack?>(track.Id == trackId ? track : null);

        public Task<bool> IsInLibraryAsync(long trackId, long requestedLibraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(track.Id == trackId && libraryId == requestedLibraryId);

        public Task<IReadOnlyList<long>> GetLibraryIdsAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>(track.Id == trackId ? [libraryId] : []);

        public Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TrackLibraryReference>>(
                track.Id == trackId ? [new TrackLibraryReference(libraryId, $"Library {libraryId}")] : []);

        public Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredTrack?>(null);

        public Task<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>> GetByRootAsync(long libraryId, long rootId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>>([]);

        public Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootAsync(long libraryId, long rootId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

        public Task EnsureMembershipAsync(long libraryId, long rootId, long trackId, string relativePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
        {
            MarkedMissing.Add(trackId);
            return Task.CompletedTask;
        }

        public Task MarkMissingBatchAsync(IReadOnlyCollection<long> trackIds, CancellationToken cancellationToken = default)
            => Task.WhenAll(trackIds.Select(id => MarkMissingAsync(id, cancellationToken)));

        public Task SaveFingerprintAsync(long trackId, AudioFingerprint fingerprint, int algorithm, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioFingerprint?>(null);
    }

    private sealed class FakeFileOperations(string existingPath) : ITrackFileOperations
    {
        public List<(string Source, string Destination)> Moves { get; } = [];

        public bool FileExists(string path)
            => string.Equals(path, existingPath, StringComparison.OrdinalIgnoreCase);

        public void Move(string sourcePath, string destinationPath)
            => Moves.Add((sourcePath, destinationPath));
    }
}
