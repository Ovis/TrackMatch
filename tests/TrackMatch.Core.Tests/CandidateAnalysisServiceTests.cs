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
        var pairRepository = new FakeCandidatePairRepository(
        [
            new CandidatePair(1, 2, 0),
            new CandidatePair(1, 3, 1),
        ]);
        var fingerprintCatalog = new FakeFingerprintCatalogRepository(
        [
            Stored(1, [1u, 2u, 3u, 4u]),
            Stored(2, [1u, 2u, 3u, 4u]),
        ]);
        var comparisonRepository = new FakeComparisonRepository();
        var service = new CandidateAnalysisService(
            fingerprintCatalog,
            pairRepository,
            comparisonRepository,
            new FingerprintComparer());

        var result = await service.AnalyzeAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(new CandidateAnalysisResult(2, 1, 1), result);
        var comparison = Assert.Single(comparisonRepository.Comparisons);
        Assert.Equal(1d, comparison.Similarity);
        Assert.Equal(1d, comparison.CoverageA);
        Assert.Equal(1d, comparison.CoverageB);
        Assert.Equal(1d, comparison.DurationRatio);
    }

    private static StoredFingerprint Stored(long id, IReadOnlyList<uint> values)
        => new(id, 2, new AudioFingerprint($"{id}.flac", TimeSpan.FromMinutes(4), values));

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

    private sealed class FakeComparisonRepository : ICandidateComparisonRepository
    {
        public IReadOnlyList<CandidateComparison> Comparisons { get; private set; } = [];

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
        {
            Comparisons = comparisons.ToArray();
            return Task.CompletedTask;
        }
    }
}
