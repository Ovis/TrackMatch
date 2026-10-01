using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Models;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.App.Tests;

public sealed class ReviewMutationServiceTests
{
    [Fact]
    public async Task CandidateReviewReadyService_ExistingComparisonから不足Classificationだけを補完する()
    {
        var pair = CandidatePairKey.Create(1, 2);
        var comparison = CreateComparison(pair);
        var comparisons = new FakeComparisonRepository(comparison);
        var classifications = new FakeClassificationRepository();
        var fingerprints = new FakeFingerprintRepository([]);
        var service = CreateReviewReadyService(
            new FakeCandidatePairRepository([]),
            fingerprints,
            comparisons,
            classifications);

        await service.EnsureReadyAsync(pair, TestContext.Current.CancellationToken);

        Assert.Equal(0, fingerprints.ReadCount);
        Assert.Equal(pair, CandidatePairKey.Create(
            Assert.Single(classifications.Items).TrackIdA,
            classifications.Items[0].TrackIdB));
    }

    [Fact]
    public async Task SupplementalReconciliation_必要Pairを1件だけ追加してClosure内の旧Supplementalを削除する()
    {
        var confirmed = new CandidateReview(
            CandidatePairKey.Create(1, 2),
            CandidateReviewDecision.ConfirmedDuplicate,
            1,
            null);
        var oldSupplemental = CandidatePairKey.Create(2, 3);
        var required = CandidatePairKey.Create(1, 3);
        var reviews = new RecordingReviewRepository(confirmed);
        var pairs = new FakeCandidatePairRepository(
        [
            new CandidatePair(1, 2, 0),
            new CandidatePair(oldSupplemental.TrackIdA, oldSupplemental.TrackIdB, -1),
        ]);
        var comparisons = new FakeComparisonRepository();
        var classifications = new FakeClassificationRepository();
        var service = new SupplementalCandidateReconciliationService(
            reviews,
            new FakeTrackLookupRepository(),
            pairs,
            CreateReviewReadyService(
                pairs,
                new FakeFingerprintRepository(CreateFingerprints(1, 2, 3)),
                comparisons,
                classifications));
        var group = new DuplicateGroup(
            1,
            10,
            null,
            DuplicateGroupKeepStatus.Unselected,
            [1, 2, 3],
            [1, 2, 3]);

        var result = await service.ReconcileAsync(
            [group],
            [1, 2, 3],
            TestContext.Current.CancellationToken);

        Assert.Contains(required, pairs.PairKeys);
        Assert.DoesNotContain(oldSupplemental, pairs.PairKeys);
        Assert.Contains(required, result.AffectedPairKeys);
        Assert.Contains(oldSupplemental, result.AffectedPairKeys);
        Assert.Equal(required, CandidatePairKey.Create(
            Assert.Single(comparisons.Items).TrackIdA,
            comparisons.Items[0].TrackIdB));
        Assert.Single(classifications.Items);
    }

    [Fact]
    public async Task SaveAsync_完全一致ならCanonicalとDerivedを更新しない()
    {
        var review = new CandidateReview(
            CandidatePairKey.Create(1, 2),
            CandidateReviewDecision.NotDuplicate,
            null,
            "同じ");
        var reviews = new RecordingReviewRepository(review);
        var groups = new MutableGroupRepository();
        var service = CreateMutationService(reviews, groups, new FakeCandidatePairRepository([]));

        var result = await service.SaveAsync(10, review, TestContext.Current.CancellationToken);

        Assert.Equal(0, reviews.SaveCount);
        Assert.Equal(0, groups.LibraryReadCount);
        Assert.Equal([review.Pair], result.AffectedPairKeys);
    }

    [Fact]
    public async Task SaveAsync_Noteだけの変更ならHistory保存だけを行う()
    {
        var pair = CandidatePairKey.Create(1, 2);
        var reviews = new RecordingReviewRepository(new CandidateReview(
            pair,
            CandidateReviewDecision.ConfirmedDuplicate,
            1,
            "変更前"));
        var groups = new MutableGroupRepository(new GlobalDuplicateGroup(1, [1, 2]));
        var service = CreateMutationService(reviews, groups, new FakeCandidatePairRepository([]));

        var result = await service.SaveAsync(
            10,
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, 1, "変更後"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reviews.SaveCount);
        Assert.Equal(0, groups.LibraryReadCount);
        Assert.Equal("変更後", Assert.Single(await reviews.GetAllAsync(TestContext.Current.CancellationToken)).Note);
        Assert.Equal([pair], result.AffectedPairKeys);
    }

    [Fact]
    public async Task SaveAsync_GroupMergeでBeforeとAfter双方のTrackをAffectedClosureへ含める()
    {
        var reviews = new RecordingReviewRepository(new CandidateReview(
            CandidatePairKey.Create(1, 2),
            CandidateReviewDecision.ConfirmedDuplicate,
            1,
            null));
        var groups = new MutableGroupRepository(new GlobalDuplicateGroup(1, [1, 2]));
        var pairs = new FakeCandidatePairRepository(
        [
            new CandidatePair(1, 2, 0),
            new CandidatePair(2, 3, 0),
        ]);
        var service = CreateMutationService(reviews, groups, pairs);

        var result = await service.SaveAsync(
            10,
            new CandidateReview(
                CandidatePairKey.Create(2, 3),
                CandidateReviewDecision.ConfirmedDuplicate,
                2,
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], result.AffectedTrackIds.Order());
        Assert.Equal(
            [1L, 2L, 3L],
            Assert.Single(await groups.GetAllGlobalAsync(TestContext.Current.CancellationToken)).TrackIds.Order());
        Assert.Contains(CandidatePairKey.Create(2, 3), result.AffectedPairKeys);
    }

    [Fact]
    public async Task DeleteAsync_GroupSplit後もBefore側TrackをCleanup範囲へ保持する()
    {
        var removedPair = CandidatePairKey.Create(2, 3);
        var oldSupplemental = CandidatePairKey.Create(1, 3);
        var reviews = new RecordingReviewRepository(
            new CandidateReview(
                CandidatePairKey.Create(1, 2),
                CandidateReviewDecision.ConfirmedDuplicate,
                1,
                null),
            new CandidateReview(
                removedPair,
                CandidateReviewDecision.ConfirmedDuplicate,
                2,
                null));
        var groups = new MutableGroupRepository(new GlobalDuplicateGroup(1, [1, 2, 3]));
        var pairs = new FakeCandidatePairRepository(
        [
            new CandidatePair(1, 2, 0),
            new CandidatePair(2, 3, 0),
            new CandidatePair(1, 3, -1),
        ]);
        var service = CreateMutationService(reviews, groups, pairs);

        var result = await service.DeleteAsync(10, removedPair, TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], result.AffectedTrackIds.Order());
        Assert.Equal(
            [1L, 2L],
            Assert.Single(await groups.GetAllGlobalAsync(TestContext.Current.CancellationToken)).TrackIds.Order());
        Assert.DoesNotContain(oldSupplemental, pairs.PairKeys);
        Assert.Contains(oldSupplemental, result.AffectedPairKeys);
    }

    [Fact]
    public async Task SaveAsync_VerdictCommit後は呼び出し元CancellationよりDerived収束を優先する()
    {
        using var cancellation = new CancellationTokenSource();
        var reviews = new CancellingReviewRepository(cancellation);
        var groups = new MutableGroupRepository();
        var pairs = new FakeCandidatePairRepository([new CandidatePair(1, 2, 0)]);
        var service = CreateMutationService(reviews, groups, pairs);

        var result = await service.SaveAsync(
            10,
            new CandidateReview(
                CandidatePairKey.Create(1, 2),
                CandidateReviewDecision.ConfirmedDuplicate,
                1,
                null),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal([1L, 2L], result.AffectedTrackIds.Order());
        Assert.All(pairs.OperationCancellationStates, Assert.False);
    }

    private static ReviewMutationService CreateMutationService(
        ICandidateReviewMutationRepository reviews,
        MutableGroupRepository groups,
        FakeCandidatePairRepository pairs)
    {
        var tracks = new FakeTrackLookupRepository();
        var comparisons = new FakeComparisonRepository();
        var classifications = new FakeClassificationRepository();
        var supplemental = new SupplementalCandidateReconciliationService(
            reviews,
            tracks,
            pairs,
            CreateReviewReadyService(
                pairs,
                new FakeFingerprintRepository(CreateFingerprints(1, 2, 3)),
                comparisons,
                classifications));
        return new ReviewMutationService(
            reviews,
            tracks,
            groups,
            new DuplicateGroupService(reviews, tracks, groups),
            supplemental);
    }

    private static CandidateReviewReadyService CreateReviewReadyService(
        ICandidatePairRepository pairs,
        IFingerprintCatalogRepository fingerprints,
        FakeComparisonRepository comparisons,
        FakeClassificationRepository classifications)
        => new(
            comparisons,
            classifications,
            new CandidateAnalysisService(fingerprints, pairs, comparisons, new FingerprintComparer()),
            new CandidateClassificationService(comparisons, classifications),
            2,
            AutomaticRelationshipClassificationProfile.Default);

    private static CandidateComparison CreateComparison(CandidatePairKey pair)
        => new(pair.TrackIdA, pair.TrackIdB, 1, 0, TimeSpan.Zero, 4,
            TimeSpan.FromSeconds(4), 1, 1, 1);

    private static IReadOnlyList<StoredFingerprint> CreateFingerprints(params long[] trackIds)
        => trackIds.Select(trackId => new StoredFingerprint(
            trackId,
            2,
            new AudioFingerprint($"{trackId}.flac", TimeSpan.FromSeconds(4), [1u, 2u, 3u, 4u]),
            DateTime.UnixEpoch)).ToArray();

    private sealed class RecordingReviewRepository(params CandidateReview[] initial)
        : ICandidateReviewMutationRepository
    {
        private readonly List<CandidateReview> _items = [.. initial];

        public int SaveCount { get; private set; }

        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _items.RemoveAll(item => item.Pair == review.Pair);
            _items.Add(review);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
        {
            _items.RemoveAll(item => item.Pair == pair);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateReview>>(_items.ToArray());

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<CandidatePairKey>>(_items.Select(item => item.Pair).ToHashSet());
    }

    private sealed class CancellingReviewRepository(CancellationTokenSource cancellation)
        : ICandidateReviewMutationRepository
    {
        private readonly List<CandidateReview> _items = [];

        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
        {
            _items.Add(review);
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateReview>>(_items.ToArray());

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<CandidatePairKey>>(_items.Select(item => item.Pair).ToHashSet());
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
            => Task.FromResult(libraryId == 10 && trackId is >= 1 and <= 10);

        public Task<IReadOnlyList<long>> GetLibraryIdsAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>([10]);

        public Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(long trackId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TrackLibraryReference>>([new TrackLibraryReference(10, "Library")]);
    }

    private sealed class MutableGroupRepository(params GlobalDuplicateGroup[] initial) : IDuplicateGroupRepository
    {
        private readonly Dictionary<long, (long? KeepTrackId, DuplicateGroupKeepStatus Status)> _keeps = [];
        private List<GlobalDuplicateGroup> _groups = [.. initial];

        public int LibraryReadCount { get; private set; }

        public Task<IReadOnlyList<GlobalDuplicateGroup>> GetAllGlobalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GlobalDuplicateGroup>>(_groups.ToArray());

        public Task<IReadOnlyList<DuplicateGroup>> GetByLibraryIdAsync(long libraryId, CancellationToken cancellationToken = default)
        {
            LibraryReadCount++;
            return Task.FromResult<IReadOnlyList<DuplicateGroup>>(_groups.Select(group => Project(group, libraryId)).ToArray());
        }

        public Task<DuplicateGroup?> GetByTrackIdAsync(long trackId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(_groups.FirstOrDefault(group => group.TrackIds.Contains(trackId)) is { } group
                ? Project(group, libraryId)
                : null);

        public Task<DuplicateGroup?> GetByIdAsync(long groupId, long libraryId, CancellationToken cancellationToken = default)
            => Task.FromResult(_groups.FirstOrDefault(group => group.Id == groupId) is { } group
                ? Project(group, libraryId)
                : null);

        public Task ReplaceGlobalAsync(IReadOnlyCollection<DuplicateGroupRebuildItem> groups, CancellationToken cancellationToken = default)
        {
            var nextId = _groups.Select(group => group.Id).DefaultIfEmpty(0).Max() + 1;
            _groups = groups.Select(group => new GlobalDuplicateGroup(
                group.ExistingGroupId ?? nextId++,
                group.TrackIds)).ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<long>> GetLibraryIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<long>>([10]);

        public Task SetDerivedKeepStateAsync(
            long libraryId,
            long groupId,
            long? keepTrackId,
            DuplicateGroupKeepStatus status,
            string changeKind,
            CancellationToken cancellationToken = default)
        {
            _keeps[groupId] = (keepTrackId, status);
            return Task.CompletedTask;
        }

        private DuplicateGroup Project(GlobalDuplicateGroup group, long libraryId)
        {
            var keep = _keeps.GetValueOrDefault(group.Id, (null, DuplicateGroupKeepStatus.Unselected));
            return new DuplicateGroup(
                group.Id,
                libraryId,
                keep.Item1,
                keep.Item2,
                group.TrackIds,
                group.TrackIds);
        }
    }

    private sealed class FakeCandidatePairRepository(IEnumerable<CandidatePair> initial) : ICandidatePairRepository
    {
        private readonly List<CandidatePair> _items = [.. initial];

        public List<bool> OperationCancellationStates { get; } = [];

        public IReadOnlySet<CandidatePairKey> PairKeys => _items
            .Select(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB))
            .ToHashSet();

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidatePair> pairs, CancellationToken cancellationToken = default)
        {
            _items.Clear();
            _items.AddRange(pairs);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidatePair>>(_items.ToArray());

        public Task<IReadOnlyList<CandidatePair>> GetWithinTracksAsync(
            IReadOnlyCollection<long> trackIds,
            CancellationToken cancellationToken = default)
        {
            OperationCancellationStates.Add(cancellationToken.IsCancellationRequested);
            var scope = trackIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<CandidatePair>>(_items
                .Where(pair => scope.Contains(pair.TrackIdA) && scope.Contains(pair.TrackIdB))
                .ToArray());
        }

        public Task EnsureSupplementalAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
        {
            OperationCancellationStates.Add(cancellationToken.IsCancellationRequested);
            if (!PairKeys.Contains(pair))
            {
                _items.Add(new CandidatePair(pair.TrackIdA, pair.TrackIdB, -1));
            }

            return Task.CompletedTask;
        }

        public Task DeleteObsoleteSupplementalWithinTracksAsync(
            IReadOnlyCollection<long> cleanupTrackIds,
            IReadOnlyCollection<CandidatePairKey> requiredPairs,
            CancellationToken cancellationToken = default)
        {
            OperationCancellationStates.Add(cancellationToken.IsCancellationRequested);
            var scope = cleanupTrackIds.ToHashSet();
            var required = requiredPairs.ToHashSet();
            _items.RemoveAll(item => item.MinimumSegmentHashDistance < 0
                && scope.Contains(item.TrackIdA)
                && scope.Contains(item.TrackIdB)
                && !required.Contains(CandidatePairKey.Create(item.TrackIdA, item.TrackIdB)));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFingerprintRepository(IReadOnlyList<StoredFingerprint> items)
        : IFingerprintCatalogRepository
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(int algorithm, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(items);
        }

        public Task<IReadOnlyList<StoredFingerprint>> GetActiveByTrackIdsAsync(
            int algorithm,
            IReadOnlyCollection<long> trackIds,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            var scope = trackIds.ToHashSet();
            return Task.FromResult<IReadOnlyList<StoredFingerprint>>(
                items.Where(item => scope.Contains(item.TrackId)).ToArray());
        }
    }

    private sealed class FakeComparisonRepository(params CandidateComparison[] initial)
        : ICandidateComparisonRepository
    {
        private readonly List<CandidateComparison> _items = [.. initial];

        public IReadOnlyList<CandidateComparison> Items => _items;

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
            => UpsertAsync(comparisons, cancellationToken);

        public Task UpsertAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
        {
            foreach (var comparison in comparisons)
            {
                var pair = CandidatePairKey.Create(comparison.TrackIdA, comparison.TrackIdB);
                _items.RemoveAll(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair);
                _items.Add(comparison);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateComparison>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateComparison>>(_items.ToArray());

        public Task<CandidateComparison?> GetAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.SingleOrDefault(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair));

        public Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<CandidatePairKey, DateTime>>(new Dictionary<CandidatePairKey, DateTime>());
    }

    private sealed class FakeClassificationRepository : ICandidateClassificationRepository
    {
        private readonly List<CandidateClassification> _items = [];

        public IReadOnlyList<CandidateClassification> Items => _items;

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateClassification> classifications, CancellationToken cancellationToken = default)
        {
            foreach (var classification in classifications)
            {
                var pair = CandidatePairKey.Create(classification.TrackIdA, classification.TrackIdB);
                _items.RemoveAll(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair);
                _items.Add(classification);
            }

            return Task.CompletedTask;
        }

        public Task<CandidateClassification?> GetAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.SingleOrDefault(item => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == pair));

        public Task<IReadOnlyList<CandidateClassificationReportRow>> GetReportAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateClassificationReportRow>>([]);
    }
}
