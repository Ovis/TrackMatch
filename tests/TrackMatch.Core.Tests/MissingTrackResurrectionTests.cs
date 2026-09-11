using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Scanning;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Missing Trackが同じRoot/RelativePathへ再出現した場合の復活挙動を検証する。
/// </summary>
public sealed class MissingTrackResurrectionTests
{
    [Fact]
    public async Task ScanAsync_ReappearedMissingTrackKeepsTrackIdAndRegeneratesFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "Album", "01.flac");
        var metadata = CreateMetadata(path);
        var stored = new StoredTrack(42, metadata, true, 1, 1, Path.Combine("Album", "01.flac"));
        var tracks = new FakeTrackRepository(stored);
        var extractor = new FakeFingerprintExtractor();
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner(LibraryScanResult.Success(metadata)),
            tracks,
            new FakeScanSessionRepository(),
            extractor,
            2);

        var result = await service.ScanAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(42, tracks.UpsertedTrackId);
        Assert.Equal([42L], tracks.DeletedFingerprintIds);
        Assert.Equal([42L], tracks.SavedFingerprintTrackIds);
        Assert.Equal([path], extractor.Paths);
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
        public List<long> DeletedFingerprintIds { get; } = [];
        public List<long> SavedFingerprintTrackIds { get; } = [];

        public Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
        {
            UpsertedTrackId = stored.Id;
            return Task.FromResult(stored.Id);
        }

        public Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredTrack?>(stored);

        public Task<IReadOnlyList<StoredTrack>> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StoredTrack>>([stored]);

        public Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

        public Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveFingerprintAsync(long trackId, AudioFingerprint fingerprint, int algorithm, CancellationToken cancellationToken = default)
        {
            SavedFingerprintTrackIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
        {
            DeletedFingerprintIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioFingerprint?>(null);
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
