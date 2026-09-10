using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Scanning;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class IncrementalLibraryScanServiceTests
{
    [Fact]
    public async Task ScanAsync_ClassifiesAddedUpdatedUnchangedAndMissingTracks()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var unchangedPath = Path.Combine(root, "unchanged.flac");
        var updatedPath = Path.Combine(root, "updated.flac");
        var addedPath = Path.Combine(root, "added.flac");
        var missingPath = Path.Combine(root, "missing.flac");
        var repository = new FakeTrackRepository(
        [
            Stored(1, Metadata(unchangedPath, 100, 10)),
            Stored(2, Metadata(updatedPath, 100, 10)),
            Stored(3, Metadata(missingPath, 100, 10)),
        ]);
        var scanner = new FakeLibraryScanner(
        [
            LibraryScanResult.Success(Metadata(unchangedPath, 100, 10)),
            LibraryScanResult.Success(Metadata(updatedPath, 200, 20)),
            LibraryScanResult.Success(Metadata(addedPath, 300, 30)),
        ]);
        var sessions = new FakeScanSessionRepository();
        var service = new IncrementalLibraryScanService(scanner, repository, sessions);

        var result = await service.ScanAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(new ScanSessionSummary(3, 3, 1, 1, 1, 0), result.Summary);
        Assert.Equal([updatedPath, addedPath], repository.UpsertedPaths);
        Assert.Equal([3L], repository.MissingTrackIds);
        Assert.Equal(result.Summary, sessions.CompletedSummary);
    }

    [Fact]
    public async Task ScanAsync_FailedFileDoesNotMakeStoredTrackMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var brokenPath = Path.Combine(root, "broken.flac");
        var repository = new FakeTrackRepository([Stored(1, Metadata(brokenPath, 100, 10))]);
        var scanner = new FakeLibraryScanner([LibraryScanResult.Failure(brokenPath, "broken")]);
        var sessions = new FakeScanSessionRepository();
        var service = new IncrementalLibraryScanService(scanner, repository, sessions);

        var result = await service.ScanAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(new ScanSessionSummary(1, 0, 0, 0, 0, 1), result.Summary);
        Assert.Empty(repository.MissingTrackIds);
    }

    private static StoredTrack Stored(long id, AudioTrackMetadata metadata) => new(id, metadata, false);

    private static AudioTrackMetadata Metadata(string path, long size, long second)
        => new(
            path,
            size,
            new DateTime(2026, 9, 10, 0, 0, (int)second, DateTimeKind.Utc),
            TimeSpan.FromMinutes(4),
            ["Artist"],
            "Title",
            "Album",
            1,
            1,
            ["J-POPS"]);

    private sealed class FakeLibraryScanner(IReadOnlyList<LibraryScanResult> results) : ILibraryScanner
    {
        public IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default) => results;
    }

    private sealed class FakeTrackRepository(IReadOnlyList<StoredTrack> initialTracks) : ITrackRepository
    {
        public List<string> UpsertedPaths { get; } = [];
        public List<long> MissingTrackIds { get; } = [];

        public Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
        {
            UpsertedPaths.Add(metadata.Path);
            var existing = initialTracks.FirstOrDefault(track => string.Equals(track.Metadata.Path, metadata.Path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(existing?.Id ?? 100L + UpsertedPaths.Count);
        }

        public Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(initialTracks.FirstOrDefault(track => string.Equals(track.Metadata.Path, path, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<StoredTrack>> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult(initialTracks);

        public Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
        {
            MissingTrackIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task SaveFingerprintAsync(long trackId, AudioFingerprint fingerprint, int algorithm, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioFingerprint?>(null);
    }

    private sealed class FakeScanSessionRepository : IScanSessionRepository
    {
        public ScanSessionSummary? CompletedSummary { get; private set; }

        public Task<long> StartAsync(string rootPath, DateTime startedAtUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(42L);

        public Task CompleteAsync(long sessionId, DateTime completedAtUtc, ScanSessionSummary summary, CancellationToken cancellationToken = default)
        {
            CompletedSummary = summary;
            return Task.CompletedTask;
        }

        public Task FailAsync(long sessionId, DateTime completedAtUtc, ScanSessionSummary summary, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
