using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

/// <summary>
/// Human Verdict Commit後の派生Global GroupとPreferred Trackの整合処理を検証する。
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
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, 1, null),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal([false], groups.ReplaceCancellationStates);
        Assert.Equal([false], groups.SetDerivedKeepCancellationStates);
        Assert.Equal(1, groups.SelectedKeepTrackId);
    }

    [Fact]
    public async Task SaveReviewAsync_ChangingPreferredTrackChangesGlobalHumanVerdict()
    {
        var pair = CandidatePairKey.Create(1, 2);
        var initial = new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, 1, null);
        var reviews = new RecordingReviewRepository(initial);
        var tracks = new FakeTrackLookupRepository();
        var groups = new RecordingGroupRepository(new GlobalDuplicateGroup(1, [1, 2]));
        var service = new DuplicateGroupService(reviews, tracks, groups);

        await service.SaveReviewAsync(
            10,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, 2, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reviews.SaveCount);
        Assert.Empty(groups.ReplaceCancellationStates);
        Assert.Equal(2, groups.SelectedKeepTrackId);
    }

    [Fact]
    public async Task SaveReviewAsync_RejectsMissingTrackBeforePersistingVerdict()
    {
        var pair = CandidatePairKey.Create(1, 2);
        var initial = new CandidateReview(pair, CandidateReviewDecision.NotDuplicate, null, null);
        var reviews = new RecordingReviewRepository(initial);
        var tracks = new FakeTrackLookupRepository(new HashSet<long> { 2 });
        var groups = new RecordingGroupRepository();
        var service = new DuplicateGroupService(reviews, tracks, groups);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveReviewAsync(
            10,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, 1, null),
            TestContext.Current.CancellationToken));

        Assert.Contains("Missing状態", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, reviews.SaveCount);
    }

    [Fact]
    public async Task SaveReviewAsync_PreservesNotDuplicateConflictAsHumanVerdict()
    {
        var reviews = new RecordingReviewRepository(
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, 1, null),
            new CandidateReview(CandidatePairKey.Create(2, 3), CandidateReviewDecision.ConfirmedDuplicate, 2, null));
        var tracks = new FakeTrackLookupRepository();
        var groups = new RecordingGroupRepository(new GlobalDuplicateGroup(1, [1, 2, 3]));
        var service = new DuplicateGroupService(reviews, tracks, groups);

        await service.SaveReviewAsync(
            10,
            new CandidateReview(CandidatePairKey.Create(1, 3), CandidateReviewDecision.NotDuplicate, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reviews.SaveCount);
        var stored = await reviews.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Contains(stored, review => review.Pair == CandidatePairKey.Create(1, 3)
            && review.Decision == CandidateReviewDecision.NotDuplicate);
        Assert.Single(DuplicateGroupConflictEvaluator.FindConflicts(stored));
    }

    [Fact]
    public async Task SaveReviewAsync_ChangingPreferredTrackThatCreatesCycleIsRejectedWithoutReplacingOldVerdict()
    {
        var targetPair = CandidatePairKey.Create(1, 3);
        var reviews = new RecordingReviewRepository(
            new CandidateReview(CandidatePairKey.Create(1, 2), CandidateReviewDecision.ConfirmedDuplicate, 1, null),
            new CandidateReview(CandidatePairKey.Create(2, 3), CandidateReviewDecision.ConfirmedDuplicate, 2, null),
            new CandidateReview(targetPair, CandidateReviewDecision.ConfirmedDuplicate, 1, null));
        var service = new DuplicateGroupService(
            reviews,
            new FakeTrackLookupRepository(),
            new RecordingGroupRepository(new GlobalDuplicateGroup(1, [1, 2, 3])));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveReviewAsync(
            10,
            new CandidateReview(targetPair, CandidateReviewDecision.ConfirmedDuplicate, 3, null),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, reviews.SaveCount);
        var stored = Assert.Single(
            await reviews.GetAllAsync(TestContext.Current.CancellationToken),
            review => review.Pair == targetPair);
        Assert.Equal(1, stored.PreferredTrackId);
    }

    [Fact]
    public async Task DeleteReviewAsync_ClearingSuspendedVerdictPreservesOtherActiveTopology()
    {
        var suspendedPair = CandidatePairKey.Create(1, 2);
        var activePair = CandidatePairKey.Create(2, 3);
        var reviews = new RecordingReviewRepository(
            new CandidateReview(suspendedPair, CandidateReviewDecision.ConfirmedDuplicate, 1, null),
            new CandidateReview(activePair, CandidateReviewDecision.ConfirmedDuplicate, 2, null));
        var tracks = new FakeTrackLookupRepository(unusableTrackIds: new HashSet<long> { 1 });
        var groups = new RecordingGroupRepository(new GlobalDuplicateGroup(1, [2, 3]));
        var service = new DuplicateGroupService(reviews, tracks, groups);

        await service.DeleteReviewAsync(10, suspendedPair, TestContext.Current.CancellationToken);

        Assert.Equal(1, reviews.DeleteCount);
        var remaining = Assert.Single(await reviews.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(activePair, remaining.Pair);
        Assert.Empty(groups.ReplaceCancellationStates);
        Assert.Equal(2, groups.SelectedKeepTrackId);
    }

    [Fact]
    public async Task DeleteReviewAsync_AllowsClearingExistingVerdictWhileTrackIsMissing()
    {
        var initial = new CandidateReview(
            CandidatePairKey.Create(1, 2),
            CandidateReviewDecision.ConfirmedDuplicate,
            1,
            null);
        var reviews = new RecordingReviewRepository(initial);
        var tracks = new FakeTrackLookupRepository(new HashSet<long> { 2 });
        var groups = new RecordingGroupRepository();
        var service = new DuplicateGroupService(reviews, tracks, groups);

        await service.DeleteReviewAsync(10, initial.Pair, TestContext.Current.CancellationToken);

        Assert.Equal(1, reviews.DeleteCount);
        Assert.Empty(await reviews.GetAllAsync(TestContext.Current.CancellationToken));
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

    private sealed class RecordingReviewRepository : ICandidateReviewMutationRepository
    {
        private readonly List<CandidateReview> _reviews;

        public RecordingReviewRepository(params CandidateReview[] initial) => _reviews = [.. initial];

        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _reviews.RemoveAll(item => item.Pair == review.Pair);
            _reviews.Add(review);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            _reviews.RemoveAll(item => item.Pair == pair);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateReview>>(_reviews.ToArray());

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<CandidatePairKey>>(_reviews.Select(item => item.Pair).ToHashSet());
    }

    private sealed class FakeTrackLookupRepository(
        IReadOnlySet<long>? missingTrackIds = null,
        IReadOnlySet<long>? unusableTrackIds = null) : ITrackLookupRepository
    {
        private readonly IReadOnlySet<long> _missingTrackIds = missingTrackIds ?? new HashSet<long>();
        private readonly IReadOnlySet<long> _unusableTrackIds = unusableTrackIds ?? new HashSet<long>();

        public Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredTrack?>(new StoredTrack(
                trackId,
                new AudioTrackMetadata(
                    $"{trackId}.flac", 100, DateTime.UnixEpoch, TimeSpan.FromMinutes(3), ["Artist"],
                    $"Track {trackId}", "Album", 1, 1, ["Genre"]),
                _missingTrackIds.Contains(trackId)));

        public Task<bool> IsInLibraryAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(libraryId == 10 && trackId is >= 1 and <= 3);

        public Task<IReadOnlyList<long>> GetLibraryIdsAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>([10]);

        public Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TrackLibraryReference>>([new TrackLibraryReference(10, "Library")]);

        public Task<bool> IsHumanVerdictUsableAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult(!_unusableTrackIds.Contains(trackId));
    }

    private sealed class RecordingGroupRepository : IDuplicateGroupRepository
    {
        private GlobalDuplicateGroup? _group;

        public RecordingGroupRepository(GlobalDuplicateGroup? initialGroup = null) => _group = initialGroup;

        public List<bool> ReplaceCancellationStates { get; } = [];
        public List<bool> SetDerivedKeepCancellationStates { get; } = [];
        public long? SelectedKeepTrackId { get; private set; }

        public Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GlobalDuplicateGroup>>(_group is null ? [] : [_group]);

        public Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DuplicateGroup>>(_group is null
                ? []
                : [CreateProjection(libraryId)]);

        public Task<DuplicateGroup?> GetByTrackIdAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(_group is null || !_group.TrackIds.Contains(trackId) ? null : CreateProjection(libraryId));

        public Task<DuplicateGroup?> GetByIdAsync(long groupId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(_group?.Id == groupId ? CreateProjection(libraryId) : null);

        public Task ReplaceGlobalAsync(IReadOnlyCollection<DuplicateGroupRebuildItem> groups, CancellationToken cancellationToken = default)
        {
            ReplaceCancellationStates.Add(cancellationToken.IsCancellationRequested);
            var plan = Assert.Single(groups);
            _group = new GlobalDuplicateGroup(plan.ExistingGroupId ?? 1, plan.TrackIds);
            return Task.CompletedTask;
        }

        public Task SetDerivedKeepStateAsync(
            long libraryId,
            long groupId,
            long? keepTrackId,
            DuplicateGroupKeepStatus status,
            string changeKind,
            CancellationToken cancellationToken = default)
        {
            SetDerivedKeepCancellationStates.Add(cancellationToken.IsCancellationRequested);
            SelectedKeepTrackId = status == DuplicateGroupKeepStatus.Selected ? keepTrackId : null;
            return Task.CompletedTask;
        }

        private DuplicateGroup CreateProjection(long libraryId)
            => new(_group!.Id, libraryId, SelectedKeepTrackId,
                SelectedKeepTrackId is null ? DuplicateGroupKeepStatus.Unselected : DuplicateGroupKeepStatus.Selected,
                _group.TrackIds, _group.TrackIds);
    }
}
