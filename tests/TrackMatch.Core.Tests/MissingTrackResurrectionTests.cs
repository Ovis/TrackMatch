using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Scanning;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// MissingになったGlobal Trackが同じ物理Pathへ再出現した場合の復活挙動を検証する。
/// </summary>
public sealed class MissingTrackResurrectionTests
{
    [Fact]
    public async Task ScanAsync_ReappearedMissingTrackKeepsTrackIdAndRestoresMembership()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "Album", "01.flac");
        var metadata = CreateMetadata(path);
        var stored = new StoredTrack(42, metadata, true);
        var tracks = new FakeTrackRepository(stored);
        var extractor = new FakeFingerprintExtractor();
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner(LibraryScanResult.Success(metadata)),
            tracks,
            new FakeScanSessionRepository(),
            extractor,
            2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal(42, tracks.UpsertedTrackId);
        Assert.Equal([(1L, 1L, 42L, Path.Combine("Album", "01.flac"))], tracks.Memberships);
        Assert.Empty(extractor.Paths);
        Assert.Equal(1, result.Summary.UpdatedFiles);
        Assert.Equal(0, result.Summary.AddedFiles);
    }

    private static AudioTrackMetadata CreateMetadata(string path)
        => new(
            path,
            100,
            new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            "Title",
            "Album",
            1,
            1,
            ["J-POPS"]);

    private sealed class FakeLibraryScanner(LibraryScanResult result) : ILibraryScanner
    {
        public IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default)
        {
            yield return result;
        }
    }

    private sealed class FakeTrackRepository(StoredTrack stored) : ITrackRepository
    {
        public long? UpsertedTrackId { get; private set; }
        public List<(long LibraryId, long RootId, long TrackId, string RelativePath)> Memberships { get; } = [];

        public Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
        {
            UpsertedTrackId = stored.Id;
            return Task.FromResult(stored.Id);
        }

        public Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredTrack?>(stored);

        public Task<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>> GetByRootAsync(
            long libraryId,
            long rootId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>>(
            [
                (new StoredLibraryTrack(libraryId, stored.Id, rootId, Path.Combine("Album", "01.flac"), false, null), stored),
            ]);

        public Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootAsync(
            long libraryId,
            long rootId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

        public Task EnsureMembershipAsync(
            long libraryId,
            long rootId,
            long trackId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            Memberships.Add((libraryId, rootId, trackId, relativePath));
            return Task.CompletedTask;
        }

        public Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task MarkMissingBatchAsync(IReadOnlyCollection<long> trackIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveFingerprintAsync(long trackId, AudioFingerprint fingerprint, int algorithm, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioFingerprint?>(new AudioFingerprint(stored.Metadata.Path, stored.Metadata.Duration, [1u, 2u, 3u]));
    }

    private sealed class FakeFingerprintExtractor : IFingerprintExtractor
    {
        public List<string> Paths { get; } = [];

        public Task<AudioFingerprint> ExtractAsync(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            return Task.FromResult(new AudioFingerprint(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u]));
        }
    }

    private sealed class FakeScanSessionRepository : IScanSessionRepository
    {
        public Task<long> StartAsync(string rootPath, DateTime startedAtUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(1L);

        public Task CompleteAsync(long sessionId, DateTime completedAtUtc, ScanSessionSummary summary, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FailAsync(long sessionId, DateTime completedAtUtc, ScanSessionSummary summary, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
