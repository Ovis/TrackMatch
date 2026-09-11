using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Trash;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class RejectedTrackTrashServiceTests
{
    [Fact]
    public async Task ProcessAsync_DryRunPreservesRelativePathWithoutMoving()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var source = Path.Combine(root, "Album", "track.flac");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
        ]);
        var tracks = new FakeTrackRepository([Stored(2, source, root)]);
        var files = new FakeFileOperations([source]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(root, trash, execute: false, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(RejectedTrackMoveStatus.Ready, item.Status);
        Assert.Equal(Path.Combine(trash, "Album", "track.flac"), item.DestinationPath);
        Assert.Empty(files.Moves);
        Assert.Empty(tracks.MarkedMissing);
        Assert.Empty(tracks.DeletedFingerprints);
    }

    [Fact]
    public async Task ProcessAsync_ExecuteMovesAndMarksTrackMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var source = Path.Combine(root, "track.flac");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(10, 20), CandidateReviewDecision.ConfirmedDuplicate, null, 10),
        ]);
        var tracks = new FakeTrackRepository([Stored(20, source, root)]);
        var files = new FakeFileOperations([source]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(root, trash, execute: true, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(RejectedTrackMoveStatus.Moved, item.Status);
        Assert.Single(files.Moves);
        Assert.Equal([20L], tracks.DeletedFingerprints);
        Assert.Equal([20L], tracks.MarkedMissing);
    }

    [Fact]
    public async Task ProcessAsync_BlocksTrackThatIsKeepAndRejectAcrossReviews()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
            new CandidateReview(CandidatePairKey.Create(2, 3), CandidateReviewDecision.ConfirmedDuplicate, null, 2),
        ]);
        var tracks = new FakeTrackRepository(
        [
            Stored(2, Path.Combine(root, "2.flac"), root),
            Stored(3, Path.Combine(root, "3.flac"), root),
        ]);
        var files = new FakeFileOperations(
        [
            Path.Combine(root, "2.flac"),
            Path.Combine(root, "3.flac"),
        ]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(root, trash, execute: false, TestContext.Current.CancellationToken);

        Assert.Contains(result.Items, item => item.TrackId == 2 && item.Status == RejectedTrackMoveStatus.ReviewConflict);
        Assert.Contains(result.Items, item => item.TrackId == 3 && item.Status == RejectedTrackMoveStatus.Ready);
    }

    [Fact]
    public async Task ProcessAsync_RejectsTrashInsideLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var service = new RejectedTrackTrashService(
            new FakeReviewRepository([]),
            new FakeTrackRepository([]),
            new FakeTrackRepository([]),
            new FakeFileOperations([]));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ProcessAsync(
            root,
            Path.Combine(root, "Trash"),
            execute: false,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessAsync_BlocksExistingDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var source = Path.Combine(root, "track.flac");
        var destination = Path.Combine(trash, "track.flac");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
        ]);
        var tracks = new FakeTrackRepository([Stored(2, source, root)]);
        var files = new FakeFileOperations([source, destination]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(root, trash, execute: true, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(RejectedTrackMoveStatus.DestinationExists, item.Status);
        Assert.Empty(files.Moves);
    }

    private static StoredTrack Stored(long id, string path, string root, bool isMissing = false)
        => new(
            id,
            new AudioTrackMetadata(
                path,
                100,
                new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc),
                TimeSpan.FromMinutes(4),
                ["Artist"],
                "Title",
                "Album",
                1,
                1,
                ["J-POPS"]),
            isMissing,
            1,
            1,
            Path.GetRelativePath(root, path));

    private sealed class FakeReviewRepository(IReadOnlyList<CandidateReview> reviews) : ICandidateReviewRepository
    {
        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(reviews);

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<CandidatePairKey>>(new HashSet<CandidatePairKey>());
    }

    private sealed class FakeTrackRepository(IReadOnlyList<StoredTrack> tracks) : ITrackRepository, ITrackLookupRepository
    {
        public List<long> MarkedMissing { get; } = [];
        public List<long> DeletedFingerprints { get; } = [];

        public Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult(tracks.FirstOrDefault(track => track.Id == trackId));

        public Task<long> UpsertMetadataAsync(AudioTrackMetadata metadata, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoredTrack?> GetByPathAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(tracks.FirstOrDefault(track => string.Equals(track.Metadata.Path, path, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<StoredTrack>> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult(tracks);

        public Task<IReadOnlySet<long>> GetTrackIdsWithoutFingerprintByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

        public Task MarkMissingAsync(long trackId, CancellationToken cancellationToken = default)
        {
            MarkedMissing.Add(trackId);
            return Task.CompletedTask;
        }

        public Task SaveFingerprintAsync(long trackId, AudioFingerprint fingerprint, int algorithm, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
        {
            DeletedFingerprints.Add(trackId);
            return Task.CompletedTask;
        }

        public Task<AudioFingerprint?> GetFingerprintAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<AudioFingerprint?>(null);
    }

    private sealed class FakeFileOperations(IEnumerable<string> existingPaths) : ITrackFileOperations
    {
        private readonly HashSet<string> _paths = new(existingPaths, StringComparer.OrdinalIgnoreCase);

        public List<(string Source, string Destination)> Moves { get; } = [];

        public bool FileExists(string path) => _paths.Contains(path);

        public void Move(string sourcePath, string destinationPath)
        {
            Moves.Add((sourcePath, destinationPath));
            _paths.Remove(sourcePath);
            _paths.Add(destinationPath);
        }
    }
}
