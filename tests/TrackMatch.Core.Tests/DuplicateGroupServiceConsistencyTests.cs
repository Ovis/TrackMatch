using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Human Verdict Commit後の派生Global Group整合処理と、Global Verdict / Library Keep分離を検証する。
/// </summary>
public sealed class DuplicateGroupServiceConsistencyTests
{
    [Fact]
    public async Task SaveReviewAsync_AfterVerdictCommitRebuildAndKeepIgnoreCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reviews = new CancellingReviewRepository(cancellation);
        var tracks = new FakeTrackLookupRepository();
        var groups = new RecordingGroupRepository();
        var service = new DuplicateGroupService(reviews, tracks, groups);

        await service.SaveReviewAsync(
            10,
            new CandidateReview(
                CandidatePairKey.Create(1, 2),
                CandidateReviewDecision.ConfirmedDuplicate,
                null),
            keepTrackId: 1,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal([false], groups.ReplaceCancellationStates);
        Assert.Equal([false], groups.SetKeepCancellationStates);
        Assert.Equal(1, groups.SelectedKeepTrackId);
    }

    [Fact]
    public async Task SaveReviewAsync_SameGlobalVerdictChangesOnlyLibraryKeep()
    {
        var initial = new CandidateReview(
            CandidatePairKey.Create(1, 2),
            CandidateReviewDecision.ConfirmedDuplicate,
            null);
        var reviews = new RecordingReviewRepository(initial);
        var tracks = new FakeTrackLookupRepository();
        var groups = new RecordingGroupRepository(new GlobalDuplicateGroup(1, [1, 2]));
        var service = new DuplicateGroupService(reviews, tracks, groups);

        await service.SaveReviewAsync(
            10,
            new CandidateReview(initial.Pair, CandidateReviewDecision.ConfirmedDuplicate, null),
            keepTrackId: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, reviews.SaveCount);
        Assert.Empty(groups.ReplaceCancellationStates);
        Assert.Equal(2, groups.SelectedKeepTrackId);
    }

    private sealed class CancellingReviewRepository(CancellationTokenSource cancellation)
        : ICandidateReviewMutationRepository
    {
        private readonly List<CandidateReview> _reviews = [];

        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
        {
            _reviews.RemoveAll(item => item.Pair == review.Pair);
            _reviews.Add(review);
            cancellation.Cancel();
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

    private sealed class RecordingReviewRepository(CandidateReview initial) : ICandidateReviewMutationRepository
    {
        private readonly List<CandidateReview> _reviews = [initial];
        public int SaveCount { get; private set; }

        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
        {
            SaveCount++;
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

    private sealed class FakeTrackLookupRepository : ITrackLookupRepository
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
            => Task.FromResult(libraryId == 10 && trackId is 1 or 2);

        public Task<IReadOnlyList<long>> GetLibraryIdsAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>([10]);

        public Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TrackLibraryReference>>([new TrackLibraryReference(10, "Library")]);
    }

    private sealed class RecordingGroupRepository : IDuplicateGroupRepository
    {
        private GlobalDuplicateGroup? _group;

        public RecordingGroupRepository(GlobalDuplicateGroup? initialGroup = null)
        {
            _group = initialGroup;
        }

        public List<bool> ReplaceCancellationStates { get; } = [];
        public List<bool> SetKeepCancellationStates { get; } = [];
        public long? SelectedKeepTrackId { get; private set; }

        public Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GlobalDuplicateGroup>>(_group is null ? [] : [_group]);

        public Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DuplicateGroup>>(_group is null
                ? []
                : [new DuplicateGroup(_group.Id, libraryId, SelectedKeepTrackId, SelectedKeepTrackId is null ? DuplicateGroupKeepStatus.Unselected : DuplicateGroupKeepStatus.Selected, _group.TrackIds, _group.TrackIds)]);

        public Task<DuplicateGroup?> GetByTrackIdAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(_group is null || !_group.TrackIds.Contains(trackId)
                ? null
                : new DuplicateGroup(_group.Id, libraryId, SelectedKeepTrackId, SelectedKeepTrackId is null ? DuplicateGroupKeepStatus.Unselected : DuplicateGroupKeepStatus.Selected, _group.TrackIds, _group.TrackIds));

        public Task<DuplicateGroup?> GetByIdAsync(long groupId, long libraryId, CancellationToken cancellationToken = default)
            => GetByTrackIdAsync(1, libraryId, cancellationToken);

        public Task ReplaceGlobalAsync(IReadOnlyCollection<DuplicateGroupRebuildItem> groups, CancellationToken cancellationToken = default)
        {
            ReplaceCancellationStates.Add(cancellationToken.IsCancellationRequested);
            var plan = Assert.Single(groups);
            _group = new GlobalDuplicateGroup(plan.ExistingGroupId ?? 1, plan.TrackIds);
            return Task.CompletedTask;
        }

        public Task SetKeepAsync(long libraryId, long groupId, long keepTrackId, string changeKind, CancellationToken cancellationToken = default)
        {
            SetKeepCancellationStates.Add(cancellationToken.IsCancellationRequested);
            SelectedKeepTrackId = keepTrackId;
            return Task.CompletedTask;
        }
    }
}
