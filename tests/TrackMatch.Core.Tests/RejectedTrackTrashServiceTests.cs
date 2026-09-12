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
    public async Task ProcessAsync_DryRunPreservesAbsolutePathStructure()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var source = Path.Combine(root, "Album", "track.flac");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
        ]);
        var tracks = new FakeTrackRepository([Stored(2, source, root, libraryId: 1)]);
        var files = new FakeFileOperations([source]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(1, trash, execute: false, cancellationToken: TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(RejectedTrackMoveStatus.Ready, item.Status);
        Assert.Equal(TrashPathRules.CreateDestinationPath(trash, source), item.DestinationPath);
        Assert.Empty(files.Moves);
    }

    [Fact]
    public async Task ProcessAsync_OnlyProcessesSelectedLibrary()
    {
        var rootA = Path.Combine(Path.GetTempPath(), "TrackMatch", "A");
        var rootB = Path.Combine(Path.GetTempPath(), "TrackMatch", "B");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var sourceA = Path.Combine(rootA, "a.flac");
        var sourceB = Path.Combine(rootB, "b.flac");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
            new CandidateReview(CandidatePairKey.Create(3, 4), CandidateReviewDecision.ConfirmedDuplicate, null, 3),
        ]);
        var tracks = new FakeTrackRepository(
        [
            Stored(2, sourceA, rootA, libraryId: 10),
            Stored(4, sourceB, rootB, libraryId: 20),
        ]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, new FakeFileOperations([sourceA, sourceB]));

        var result = await service.ProcessAsync(10, trash, execute: false, cancellationToken: TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(2, item.TrackId);
    }

    [Fact]
    public async Task ProcessAsync_RenameCollisionUsesSmallestAvailableNumber()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var source = Path.Combine(root, "track.flac");
        var destination = TrashPathRules.CreateDestinationPath(trash, source);
        var directory = Path.GetDirectoryName(destination)!;
        var second = Path.Combine(directory, "track (2).flac");
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
        ]);
        var tracks = new FakeTrackRepository([Stored(2, source, root, libraryId: 1)]);
        var files = new FakeFileOperations([source, destination, second]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(
            1,
            trash,
            execute: true,
            collisionBehavior: TrashDestinationCollisionBehavior.Rename,
            cancellationToken: TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(RejectedTrackMoveStatus.Moved, item.Status);
        Assert.Equal(Path.Combine(directory, "track (3).flac"), item.DestinationPath);
        Assert.Single(files.Moves);
    }

    [Fact]
    public async Task ProcessAsync_SkipCollisionNeverOverwrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrackMatch", "Music");
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var source = Path.Combine(root, "track.flac");
        var destination = TrashPathRules.CreateDestinationPath(trash, source);
        var reviews = new FakeReviewRepository(
        [
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null, 1),
        ]);
        var tracks = new FakeTrackRepository([Stored(2, source, root, libraryId: 1)]);
        var files = new FakeFileOperations([source, destination]);
        var service = new RejectedTrackTrashService(reviews, tracks, tracks, files);

        var result = await service.ProcessAsync(
            1,
            trash,
            execute: true,
            collisionBehavior: TrashDestinationCollisionBehavior.Skip,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RejectedTrackMoveStatus.DestinationExists, Assert.Single(result.Items).Status);
        Assert.Empty(files.Moves);
        Assert.Empty(tracks.MarkedMissing);
    }

    [Fact]
    public void ValidateRootSeparation_RejectsBothContainmentDirections()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "TrackMatch", "Separation");
        var library = Path.Combine(basePath, "Library");

        Assert.Throws<ArgumentException>(() => TrashPathRules.ValidateRootSeparation(library, [library]));
        Assert.Throws<ArgumentException>(() => TrashPathRules.ValidateRootSeparation(basePath, [library]));
        Assert.Throws<ArgumentException>(() => TrashPathRules.ValidateRootSeparation(Path.Combine(library, "Trash"), [library]));
    }

    [Fact]
    public void CreateDestinationPath_UncPathUsesUncPrefix()
    {
        var trash = Path.Combine(Path.GetTempPath(), "TrackMatch", "Trash");
        var result = TrashPathRules.CreateDestinationPath(trash, @"\\NAS\Music\Anime\a.flac");

        Assert.Equal(Path.Combine(trash, "UNC", "NAS", "Music", "Anime", "a.flac"), result);
    }

    private static StoredTrack Stored(long id, string path, string root, long libraryId, bool isMissing = false)
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
            libraryId,
            libraryId,
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
