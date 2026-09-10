using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class CandidateReviewTests
{
    [Fact]
    public void PairKey_NormalizesTrackOrder()
    {
        Assert.Equal(new CandidatePairKey(1, 2), CandidatePairKey.Create(2, 1));
    }

    [Fact]
    public async Task GenerateAsync_ExcludesNotDuplicateReview()
    {
        var values = Enumerable.Repeat(0u, 300).ToArray();
        var fingerprints = new FakeFingerprintCatalogRepository(
        [
            Stored(1, values),
            Stored(2, values),
        ]);
        var pairRepository = new FakeCandidatePairRepository();
        var reviewRepository = new FakeCandidateReviewRepository(
            new HashSet<CandidatePairKey> { CandidatePairKey.Create(1, 2) });
        var service = new CandidateGenerationService(
            fingerprints,
            pairRepository,
            reviewRepository,
            new CandidatePairGenerator(new FingerprintSegmentSketcher()));

        var result = await service.GenerateAsync(2, new CandidateGenerationOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Pairs);
        Assert.Empty(pairRepository.Pairs);
    }

    private static StoredFingerprint Stored(long id, IReadOnlyList<uint> values)
        => new(id, 2, new AudioFingerprint($"{id}.flac", TimeSpan.FromMinutes(4), values));

    private sealed class FakeFingerprintCatalogRepository(IReadOnlyList<StoredFingerprint> items)
        : IFingerprintCatalogRepository
    {
        public Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(int algorithm, CancellationToken cancellationToken = default)
            => Task.FromResult(items);
    }

    private sealed class FakeCandidatePairRepository : ICandidatePairRepository
    {
        public IReadOnlyList<CandidatePair> Pairs { get; private set; } = [];

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidatePair> newPairs, CancellationToken cancellationToken = default)
        {
            Pairs = newPairs.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Pairs);
    }

    private sealed class FakeCandidateReviewRepository(IReadOnlySet<CandidatePairKey> excluded) : ICandidateReviewRepository
    {
        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(excluded);
    }
}
