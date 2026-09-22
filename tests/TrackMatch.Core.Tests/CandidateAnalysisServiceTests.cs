using TrackMatch.Core.Candidates;
using TrackMatch.Core.Comparison;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class CandidateAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_ComparesAvailableCandidatePairsAndSkipsStalePairs()
    {
        var extractedAt = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var pairRepository = new FakeCandidatePairRepository(
        [
            new CandidatePair(1, 2, 0),
            new CandidatePair(1, 3, 1),
        ]);
        var fingerprintCatalog = new FakeFingerprintCatalogRepository(
        [
            Stored(1, [1u, 2u, 3u, 4u], extractedAt),
            Stored(2, [1u, 2u, 3u, 4u], extractedAt),
        ]);
        var comparisonRepository = new FakeComparisonRepository();
        var service = new CandidateAnalysisService(
            fingerprintCatalog,
            pairRepository,
            comparisonRepository,
            new FingerprintComparer());

        var result = await service.AnalyzeAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(new CandidateAnalysisResult(2, 1, 0, 1), result);
        var comparison = Assert.Single(comparisonRepository.Comparisons);
        Assert.Equal(1d, comparison.Similarity);
        Assert.Equal(1d, comparison.CoverageA);
        Assert.Equal(1d, comparison.CoverageB);
        Assert.Equal(1d, comparison.DurationRatio);
    }

    [Fact]
    public async Task AnalyzeAsync_ReusesComparisonWhenFingerprintsAreOlderThanComparison()
    {
        var extractedAt = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var pair = new CandidatePair(1, 2, 0);
        var comparisonRepository = new FakeComparisonRepository(
            new Dictionary<CandidatePairKey, DateTime>
            {
                [CandidatePairKey.Create(1, 2)] = extractedAt.AddMinutes(1),
            });
        var service = new CandidateAnalysisService(
            new FakeFingerprintCatalogRepository(
            [
                Stored(1, [1u, 2u, 3u, 4u], extractedAt),
                Stored(2, [1u, 2u, 3u, 4u], extractedAt),
            ]),
            new FakeCandidatePairRepository([pair]),
            comparisonRepository,
            new FingerprintComparer());

        var result = await service.AnalyzeAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(new CandidateAnalysisResult(1, 0, 1, 0), result);
        Assert.Empty(comparisonRepository.Comparisons);
    }

    [Fact]
    public async Task AnalyzeAsync_RecomparesWhenEitherFingerprintIsNewerThanComparison()
    {
        var comparedAt = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var comparisonRepository = new FakeComparisonRepository(
            new Dictionary<CandidatePairKey, DateTime>
            {
                [CandidatePairKey.Create(1, 2)] = comparedAt,
            });
        var service = new CandidateAnalysisService(
            new FakeFingerprintCatalogRepository(
            [
                Stored(1, [1u, 2u, 3u, 4u], comparedAt.AddMinutes(1)),
                Stored(2, [1u, 2u, 3u, 4u], comparedAt.AddMinutes(-1)),
            ]),
            new FakeCandidatePairRepository([new CandidatePair(1, 2, 0)]),
            comparisonRepository,
            new FingerprintComparer());

        var result = await service.AnalyzeAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(new CandidateAnalysisResult(1, 1, 0, 0), result);
        Assert.Single(comparisonRepository.Comparisons);
    }


    [Fact]
    public async Task AnalyzeAsync_CheckpointsCompletedComparisonsBeforeLaterCancellation()
    {
        const int checkpointSize = 500;
        var extractedAt = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var pairs = Enumerable.Range(2, checkpointSize + 1)
            .Select(trackId => new CandidatePair(1, trackId, 0))
            .ToArray();
        var fingerprints = Enumerable.Range(1, checkpointSize + 2)
            .Select(trackId => Stored(trackId, [1u, 2u, 3u, 4u], extractedAt))
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        var comparisonRepository = new CancellingComparisonRepository(cancellation, checkpointSize);
        var service = new CandidateAnalysisService(
            new FakeFingerprintCatalogRepository(fingerprints),
            new FakeCandidatePairRepository(pairs),
            comparisonRepository,
            new FingerprintComparer());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AnalyzeAsync(2, cancellation.Token));

        Assert.Equal(checkpointSize, comparisonRepository.Comparisons.Count);
        Assert.Equal(1, comparisonRepository.UpsertCount);
    }

    private static StoredFingerprint Stored(long id, IReadOnlyList<uint> values, DateTime extractedAt)
        => new(id, 2, new AudioFingerprint($"{id}.flac", TimeSpan.FromMinutes(4), values), extractedAt);

    private sealed class FakeFingerprintCatalogRepository(IReadOnlyList<StoredFingerprint> items)
        : IFingerprintCatalogRepository
    {
        public Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(int algorithm, CancellationToken cancellationToken = default)
            => Task.FromResult(items);
    }

    private sealed class FakeCandidatePairRepository(IReadOnlyList<CandidatePair> pairs) : ICandidatePairRepository
    {
        public Task ReplaceAllAsync(IReadOnlyCollection<CandidatePair> newPairs, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(pairs);
    }


    /// <summary>
    /// 最初のCheckpoint保存直後にCancelし、そこまでの比較結果が永続化境界へ渡されたことを検証する。
    /// </summary>
    private sealed class CancellingComparisonRepository(
        CancellationTokenSource cancellation,
        int expectedCheckpointSize) : ICandidateComparisonRepository
    {
        public List<CandidateComparison> Comparisons { get; } = [];

        public int UpsertCount { get; private set; }

        public Task ReplaceAllAsync(
            IReadOnlyCollection<CandidateComparison> comparisons,
            CancellationToken cancellationToken = default)
            => UpsertAsync(comparisons, cancellationToken);

        public Task UpsertAsync(
            IReadOnlyCollection<CandidateComparison> comparisons,
            CancellationToken cancellationToken = default)
        {
            Comparisons.AddRange(comparisons);
            UpsertCount++;
            if (comparisons.Count == expectedCheckpointSize)
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateComparison>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateComparison>>(Comparisons);

        public Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<CandidatePairKey, DateTime>>(
                new Dictionary<CandidatePairKey, DateTime>());
    }

    private sealed class FakeComparisonRepository(
        IReadOnlyDictionary<CandidatePairKey, DateTime>? comparedAt = null) : ICandidateComparisonRepository
    {
        private readonly IReadOnlyDictionary<CandidatePairKey, DateTime> _comparedAt = comparedAt
            ?? new Dictionary<CandidatePairKey, DateTime>();

        public IReadOnlyList<CandidateComparison> Comparisons { get; private set; } = [];

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
        {
            Comparisons = comparisons.ToArray();
            return Task.CompletedTask;
        }

        public Task UpsertAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
        {
            Comparisons = comparisons.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateComparison>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Comparisons);

        public Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_comparedAt);
    }
}
