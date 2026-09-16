using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// レビュー省略Pairの明示確定がKeepを変更せず、通常のGlobal ConfirmedDuplicate edgeとして保存されることを検証する。
/// </summary>
public sealed class SkippedPairReviewServiceTests
{
    [Fact]
    public async Task ConfirmAsync_NonKeepPairInSelectedGroup_SavesVerdictWithoutChangingKeep()
    {
        var reviews = CreateBaseReviews();
        var groups = new FakeGroupRepository(keepTrackId: 1);
        var service = new SkippedPairReviewService(reviews, new FakeTrackRepository(), groups);

        await service.ConfirmAsync(10, CandidatePairKey.Create(2, 3), TestContext.Current.CancellationToken);

        var stored = await reviews.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Contains(stored, review => review.Pair == CandidatePairKey.Create(2, 3)
            && review.Decision == CandidateReviewDecision.ConfirmedDuplicate);
        Assert.Equal(1, groups.KeepTrackId);
        Assert.Equal(0, groups.SetKeepCount);
    }

    [Fact]
    public async Task ConfirmAsync_PairContainsKeep_RejectsBeforePersisting()
    {
        var reviews = CreateBaseReviews();
        var groups = new FakeGroupRepository(keepTrackId: 2);
        var service = new SkippedPairReviewService(reviews, new FakeTrackRepository(), groups);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(
            10,
            CandidatePairKey.Create(2, 3),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, (await reviews.GetAllAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task ConfirmAsync_KeepIsNotSelected_RejectsBeforePersisting()
    {
        var reviews = CreateBaseReviews();
        var groups = new FakeGroupRepository(keepTrackId: null);
        var service = new SkippedPairReviewService(reviews, new FakeTrackRepository(), groups);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(
            10,
            CandidatePairKey.Create(2, 3),
            TestContext.Current.CancellationToken));

        Assert.Equal(2, (await reviews.GetAllAsync(TestContext.Current.CancellationToken)).Count);
    }

    private static FakeReviewRepository CreateBaseReviews()
        => new(
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, null),
            new CandidateReview(CandidatePairKey.Create(1, 3), CandidateReviewDecision.ConfirmedDuplicate, null));

    private sealed class FakeReviewRepository(params CandidateReview[] initial) : ICandidateReviewMutationRepository
    {
        private readonly List<CandidateReview> _reviews = [.. initial];

        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
        {
            _reviews.RemoveAll(item => item.Pair == review.Pair);
            _reviews.Add(review);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
        {
            _reviews.RemoveAll(item => item.Pair == pair);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateReview>>(_reviews.ToArray());

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<CandidatePairKey>>(_reviews.Select(item => item.Pair).ToHashSet());
    }

    private sealed class FakeTrackRepository : ITrackLookupRepository
    {
        public Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredTrack?>(new StoredTrack(
                trackId,
                new AudioTrackMetadata(
                    $"{trackId}.flac",
                    100,
                    DateTime.UnixEpoch,
                    TimeSpan.FromMinutes(3),
                    ["Artist"],
                    $"Track {trackId}",
                    "Album",
                    1,
                    1,
                    ["Genre"]),
                false));

        public Task<bool> IsInLibraryAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(libraryId == 10 && trackId is >= 1 and <= 3);

        public Task<IReadOnlyList<long>> GetLibraryIdsAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>([10]);

        public Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TrackLibraryReference>>([new TrackLibraryReference(10, "Library")]);
    }

    private sealed class FakeGroupRepository(long? keepTrackId) : IDuplicateGroupRepository
    {
        private readonly GlobalDuplicateGroup _globalGroup = new(1, [1, 2, 3]);

        public long? KeepTrackId { get; } = keepTrackId;
        public int SetKeepCount { get; private set; }

        public Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GlobalDuplicateGroup>>([_globalGroup]);

        public Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DuplicateGroup>>([CreateProjection(libraryId)]);

        public Task<DuplicateGroup?> GetByTrackIdAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult<DuplicateGroup?>(_globalGroup.TrackIds.Contains(trackId) ? CreateProjection(libraryId) : null);

        public Task<DuplicateGroup?> GetByIdAsync(long groupId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult<DuplicateGroup?>(groupId == _globalGroup.Id ? CreateProjection(libraryId) : null);

        public Task ReplaceGlobalAsync(IReadOnlyCollection<DuplicateGroupRebuildItem> groups, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetKeepAsync(long libraryId, long groupId, long keepTrackId, string changeKind, CancellationToken cancellationToken = default)
        {
            SetKeepCount++;
            return Task.CompletedTask;
        }

        private DuplicateGroup CreateProjection(long libraryId)
            => new(
                _globalGroup.Id,
                libraryId,
                KeepTrackId,
                KeepTrackId is null ? DuplicateGroupKeepStatus.Unselected : DuplicateGroupKeepStatus.Selected,
                _globalGroup.TrackIds,
                _globalGroup.TrackIds);
    }
}
