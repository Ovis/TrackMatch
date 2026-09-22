using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Scanning;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Global TrackとLibrary Membershipを更新する増分Scanの主要挙動を検証する。
/// </summary>
public sealed class IncrementalLibraryScanServiceTests
{
    [Fact]
    public async Task ScanAsync_GeneratesFingerprintsOnlyForAddedUpdatedOrMissingFingerprintTracks()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var unchangedPath = Path.Combine(root, "unchanged.flac");
        var retryPath = Path.Combine(root, "retry.flac");
        var updatedPath = Path.Combine(root, "updated.flac");
        var addedPath = Path.Combine(root, "added.flac");
        var missingPath = Path.Combine(root, "missing.flac");
        var repository = new FakeTrackRepository(
        [
            Stored(1, Metadata(unchangedPath, 100, 10)),
            Stored(2, Metadata(retryPath, 100, 10)),
            Stored(3, Metadata(updatedPath, 100, 10)),
            Stored(4, Metadata(missingPath, 100, 10)),
        ],
        missingFingerprintIds: new HashSet<long> { 2 });
        var scanner = new FakeLibraryScanner(
        [
            LibraryScanResult.Success(Metadata(unchangedPath, 100, 10)),
            LibraryScanResult.Success(Metadata(retryPath, 100, 10)),
            LibraryScanResult.Success(Metadata(updatedPath, 200, 20)),
            LibraryScanResult.Success(Metadata(addedPath, 300, 30)),
        ]);
        var extractor = new FakeFingerprintExtractor();
        var sessions = new FakeScanSessionRepository();
        var service = new IncrementalLibraryScanService(scanner, repository, sessions, extractor, 2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal(new ScanSessionSummary(4, 4, 1, 1, 1, 0), result.Summary);
        Assert.Equal([unchangedPath, retryPath, updatedPath, addedPath], repository.UpsertedPaths);
        Assert.Equal([retryPath, updatedPath, addedPath], extractor.Paths);
        Assert.Equal(3, repository.SavedFingerprints.Count);
        Assert.Equal([4L], repository.MissingTrackIds);
        Assert.Equal(4, repository.Memberships.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ScanAsync_MetadataOnlyChange_PreservesVerdictStateAndMarksVerificationCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "metadata-only.flac");
        var previousFingerprint = new AudioFingerprint(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u]);
        var repository = new FakeTrackRepository(
            [Stored(1, Metadata(path, 100, 10))],
            existingFingerprint: previousFingerprint);
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([LibraryScanResult.Success(Metadata(path, 200, 20))]),
            repository,
            new FakeScanSessionRepository(),
            new FakeFingerprintExtractor(),
            2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal([1L], repository.VerificationPendingTrackIds);
        Assert.Equal([1L], repository.VerifiedTrackIds);
        Assert.Empty(repository.ContentChangedTrackIds);
        Assert.Empty(result.ContentChanges);
    }

    [Fact]
    public async Task ScanAsync_AudioContentChange_InvalidatesVerdictStateAndReportsCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "audio-changed.flac");
        var previousFingerprint = new AudioFingerprint(path, TimeSpan.FromMinutes(4), [9u, 9u, 9u]);
        var repository = new FakeTrackRepository(
            [Stored(1, Metadata(path, 100, 10))],
            existingFingerprint: previousFingerprint,
            invalidatedReviewCount: 2);
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([LibraryScanResult.Success(Metadata(path, 200, 20))]),
            repository,
            new FakeScanSessionRepository(),
            new FakeFingerprintExtractor(),
            2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal([1L], repository.VerificationPendingTrackIds);
        Assert.Empty(repository.VerifiedTrackIds);
        Assert.Equal([1L], repository.ContentChangedTrackIds);
        var notice = Assert.Single(result.ContentChanges);
        Assert.Equal(path, notice.Path);
        Assert.Equal(2, notice.InvalidatedReviewCount);
    }

    [Fact]
    public async Task ScanAsync_ContentVerificationFailure_PreservesVerdictAndLeavesRetryState()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "verification-failed.flac");
        var previousFingerprint = new AudioFingerprint(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u]);
        var repository = new FakeTrackRepository(
            [Stored(1, Metadata(path, 100, 10))],
            existingFingerprint: previousFingerprint);
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([LibraryScanResult.Success(Metadata(path, 200, 20))]),
            repository,
            new FakeScanSessionRepository(),
            new FakeFingerprintExtractor(path),
            2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal([1L], repository.VerificationPendingTrackIds);
        Assert.Equal([1L], repository.VerificationFailedTrackIds);
        Assert.Empty(repository.ContentChangedTrackIds);
        Assert.Empty(repository.VerifiedTrackIds);
        Assert.Equal("Fingerprint", Assert.Single(result.Errors).Stage);
    }

    [Fact]
    public async Task ScanAsync_PreviousVerificationFailure_RetriesEvenWhenFileAttributesNoLongerDiffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "retry-verification.flac");
        var fingerprint = new AudioFingerprint(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u]);
        var repository = new FakeTrackRepository(
            [Stored(1, Metadata(path, 200, 20))],
            existingFingerprint: fingerprint,
            verificationPendingTrackIds: new HashSet<long> { 1 });
        var extractor = new FakeFingerprintExtractor();
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([LibraryScanResult.Success(Metadata(path, 200, 20))]),
            repository,
            new FakeScanSessionRepository(),
            extractor,
            2);

        await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal([path], extractor.Paths);
        Assert.Equal([1L], repository.VerifiedTrackIds);
        Assert.Empty(repository.ContentChangedTrackIds);
    }

    [Fact]
    public async Task ScanAsync_NormalCompletionMarksStoredTrackMissingWhenScannerDidNotFindIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "missing.flac");
        var repository = new FakeTrackRepository([Stored(1, Metadata(path, 100, 10))]);
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([]),
            repository,
            new FakeScanSessionRepository(),
            new FakeFingerprintExtractor(),
            2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.RemovedFiles);
        Assert.Equal([1L], repository.MissingTrackIds);
    }

    [Fact]
    public async Task ScanAsync_FingerprintFailureIsRecordedAndRetriedOnNextScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var path = Path.Combine(root, "retry.flac");
        var repository = new FakeTrackRepository(
            [Stored(1, Metadata(path, 100, 10))],
            missingFingerprintIds: new HashSet<long> { 1 });
        var extractor = new FakeFingerprintExtractor(path);
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([LibraryScanResult.Success(Metadata(path, 100, 10))]),
            repository,
            new FakeScanSessionRepository(),
            extractor,
            2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Summary.ErrorCount);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Fingerprint", error.Stage);
        Assert.Empty(repository.SavedFingerprints);
        Assert.Contains(1L, repository.MissingFingerprintIds);
    }

    [Fact]
    public async Task ScanAsync_FailedMetadataDoesNotMakeStoredTrackMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var brokenPath = Path.Combine(root, "broken.flac");
        var repository = new FakeTrackRepository([Stored(1, Metadata(brokenPath, 100, 10))]);
        var scanner = new FakeLibraryScanner([LibraryScanResult.Failure(brokenPath, "broken")]);
        var sessions = new FakeScanSessionRepository();
        var service = new IncrementalLibraryScanService(scanner, repository, sessions, new FakeFingerprintExtractor(), 2);

        var result = await service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken);

        Assert.Equal(new ScanSessionSummary(1, 0, 0, 0, 0), result.Summary);
        Assert.Empty(repository.MissingTrackIds);
        Assert.Equal("Metadata", Assert.Single(result.Errors).Stage);
    }

    [Fact]
    public async Task ScanAsync_CancellationDuringEnumerationPreservesCompletedWorkAndSkipsMissingFinalization()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var seenPath = Path.Combine(root, "seen.flac");
        var unseenPath = Path.Combine(root, "unseen.flac");
        using var cancellation = new CancellationTokenSource();
        var repository = new FakeTrackRepository(
        [
            Stored(1, Metadata(seenPath, 100, 10)),
            Stored(2, Metadata(unseenPath, 100, 10)),
        ]);
        var sessions = new FakeScanSessionRepository();
        var service = new IncrementalLibraryScanService(
            new CancellingLibraryScanner(LibraryScanResult.Success(Metadata(seenPath, 100, 10)), cancellation),
            repository,
            sessions,
            new FakeFingerprintExtractor(),
            2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync(1, 1, root, cancellation.Token));

        Assert.Equal([seenPath], repository.UpsertedPaths);
        Assert.Empty(repository.MissingTrackIds);
        Assert.NotNull(sessions.FailedSummary);
    }

    [Fact]
    public async Task ScanAsync_EnumerationFailurePreservesCompletedWorkAndSkipsMissingFinalization()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var seenPath = Path.Combine(root, "seen.flac");
        var unseenPath = Path.Combine(root, "unseen.flac");
        var repository = new FakeTrackRepository(
        [
            Stored(1, Metadata(seenPath, 100, 10)),
            Stored(2, Metadata(unseenPath, 100, 10)),
        ]);
        var sessions = new FakeScanSessionRepository();
        var service = new IncrementalLibraryScanService(
            new ThrowingLibraryScanner(LibraryScanResult.Success(Metadata(seenPath, 100, 10))),
            repository,
            sessions,
            new FakeFingerprintExtractor(),
            2);

        await Assert.ThrowsAsync<IOException>(() => service.ScanAsync(1, 1, root, TestContext.Current.CancellationToken));

        Assert.Equal([seenPath], repository.UpsertedPaths);
        Assert.Empty(repository.MissingTrackIds);
        Assert.NotNull(sessions.FailedSummary);
    }

    [Fact]
    public async Task ScanAsync_AfterMissingFinalizationStartsCallerCancellationCannotPartiallyApplyMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var firstPath = Path.Combine(root, "first.flac");
        var secondPath = Path.Combine(root, "second.flac");
        using var cancellation = new CancellationTokenSource();
        var repository = new FakeTrackRepository(
        [
            Stored(1, Metadata(firstPath, 100, 10)),
            Stored(2, Metadata(secondPath, 100, 10)),
        ],
        cancelDuringMissingBatch: cancellation);
        var service = new IncrementalLibraryScanService(
            new FakeLibraryScanner([]),
            repository,
            new FakeScanSessionRepository(),
            new FakeFingerprintExtractor(),
            2);

        var result = await service.ScanAsync(1, 1, root, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, result.Summary.RemovedFiles);
        Assert.Equal([1L, 2L], repository.MissingTrackIds.Order().ToArray());
        Assert.Equal([false], repository.MissingBatchCancellationStates);
    }

    private static StoredTrack Stored(long id, AudioTrackMetadata metadata)
        => new(id, metadata, false);

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

    private sealed class CancellingLibraryScanner(
        LibraryScanResult first,
        CancellationTokenSource cancellation) : ILibraryScanner
    {
        public IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default)
        {
            yield return first;
            cancellation.Cancel();
        }
    }

    private sealed class ThrowingLibraryScanner(LibraryScanResult first) : ILibraryScanner
    {
        public IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default)
        {
            yield return first;
            throw new IOException("enumeration failed");
        }
    }

    private sealed class FakeFingerprintExtractor(string? failingPath = null) : IFingerprintExtractor
    {
        public List<string> Paths { get; } = [];

        public Task<AudioFingerprint> ExtractAsync(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            if (string.Equals(path, failingPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("fpcalc failure");
            }

            return Task.FromResult(new AudioFingerprint(path, TimeSpan.FromMinutes(4), [1u, 2u, 3u]));
        }
    }

    private sealed class FakeTrackRepository(
        IReadOnlyList<StoredTrack> initialTracks,
        IReadOnlySet<long>? missingFingerprintIds = null,
        CancellationTokenSource? cancelDuringMissingBatch = null,
        AudioFingerprint? existingFingerprint = null,
        int invalidatedReviewCount = 0,
        IReadOnlySet<long>? verificationPendingTrackIds = null) : ITrackRepository
    {
        private long _nextId = 100;
        public List<string> UpsertedPaths { get; } = [];
        public List<long> MissingTrackIds { get; } = [];
        public List<bool> MissingBatchCancellationStates { get; } = [];
        public List<(long TrackId, AudioFingerprint Fingerprint, int Algorithm)> SavedFingerprints { get; } = [];
        public List<(long LibraryId, long RootId, long TrackId, string RelativePath)> Memberships { get; } = [];
        public HashSet<long> MissingFingerprintIds { get; } = missingFingerprintIds?.ToHashSet() ?? [];
        public List<long> VerificationPendingTrackIds { get; } = [];
        public List<long> VerificationFailedTrackIds { get; } = [];
        public List<long> VerifiedTrackIds { get; } = [];
        public List<long> ContentChangedTrackIds { get; } = [];

        public Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
        {
            UpsertedPaths.Add(metadata.Path);
            var existing = initialTracks.FirstOrDefault(track => string.Equals(track.Metadata.Path, metadata.Path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(existing?.Id ?? ++_nextId);
        }

        public Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(initialTracks.FirstOrDefault(track => string.Equals(track.Metadata.Path, path, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>> GetByRootAsync(
            long libraryId,
            long rootId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(StoredLibraryTrack Membership, StoredTrack Track)>>(
                initialTracks.Select(track => (
                    new StoredLibraryTrack(libraryId, track.Id, rootId, Path.GetFileName(track.Metadata.Path), false, null),
                    track)).ToArray());

        public Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootAsync(
            long libraryId,
            long rootId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<long>>(MissingFingerprintIds);

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
        {
            MissingTrackIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task MarkMissingBatchAsync(
            IReadOnlyCollection<long> trackIds,
            CancellationToken cancellationToken = default)
        {
            MissingBatchCancellationStates.Add(cancellationToken.IsCancellationRequested);
            cancelDuringMissingBatch?.Cancel();
            MissingTrackIds.AddRange(trackIds);
            return Task.CompletedTask;
        }

        public Task SaveFingerprintAsync(long trackId, AudioFingerprint fingerprint, int algorithm, CancellationToken cancellationToken = default)
        {
            SavedFingerprints.Add((trackId, fingerprint, algorithm));
            MissingFingerprintIds.Remove(trackId);
            return Task.CompletedTask;
        }

        public Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
        {
            MissingFingerprintIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult(existingFingerprint);

        public Task<bool> IsContentVerificationPendingAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult(verificationPendingTrackIds?.Contains(trackId) == true);

        public Task MarkContentVerificationPendingAsync(long trackId, CancellationToken cancellationToken = default)
        {
            VerificationPendingTrackIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task MarkContentVerificationFailedAsync(
            long trackId,
            CancellationToken cancellationToken = default,
            string? error = null)
        {
            VerificationFailedTrackIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task MarkContentVerifiedAsync(long trackId, CancellationToken cancellationToken = default)
        {
            VerifiedTrackIds.Add(trackId);
            return Task.CompletedTask;
        }

        public Task<int> ConfirmContentChangedAndGetInvalidatedReviewCountAsync(
            long trackId,
            CancellationToken cancellationToken = default)
        {
            ContentChangedTrackIds.Add(trackId);
            return Task.FromResult(invalidatedReviewCount);
        }
    }

    private sealed class FakeScanSessionRepository : IScanSessionRepository
    {
        public ScanSessionSummary? CompletedSummary { get; private set; }
        public ScanSessionSummary? FailedSummary { get; private set; }

        public Task<long> StartAsync(string rootPath, DateTime startedAtUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(42L);

        public Task CompleteAsync(long sessionId, DateTime completedAtUtc, ScanSessionSummary summary, CancellationToken cancellationToken = default)
        {
            CompletedSummary = summary;
            return Task.CompletedTask;
        }

        public Task FailAsync(long sessionId, DateTime completedAtUtc, ScanSessionSummary summary, CancellationToken cancellationToken = default)
        {
            FailedSummary = summary;
            return Task.CompletedTask;
        }
    }
}
