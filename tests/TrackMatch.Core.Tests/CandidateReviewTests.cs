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
    public void ConfirmedDuplicate_RequiresKeepTrackFromPair()
    {
        var pair = CandidatePairKey.Create(1, 2);

        var valid = new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, null, 2);
        valid.Validate();

        Assert.Throws<ArgumentException>(() =>
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, null).Validate());
        Assert.Throws<ArgumentException>(() =>
            new CandidateReview(pair, CandidateReviewDecision.ConfirmedDuplicate, null, 3).Validate());
    }

    [Fact]
    public void NotDuplicate_DoesNotAcceptKeepTrack()
    {
        var review = new CandidateReview(
            CandidatePairKey.Create(1, 2),
            CandidateReviewDecision.NotDuplicate,
            null,
            1);

        Assert.Throws<ArgumentException>(review.Validate);
    }

    [Fact]
    public async Task GenerateAsync_ExcludesReviewedPair()
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
        var sketcher = new FingerprintSegmentSketcher();
        var service = new CandidateGenerationService(
            fingerprints,
            new FakeSketchRepository(),
            pairRepository,
            reviewRepository,
            sketcher,
            new CandidatePairGenerator(sketcher));

        var result = await service.GenerateAsync(2, new CandidateGenerationOptions(), TestContext.Current.CancellationToken);

        Assert.Empty(result.Pairs);
        Assert.Empty(pairRepository.Pairs);
    }

    private static StoredFingerprint Stored(long id, IReadOnlyList<uint> values)
        => new(id, 2, new AudioFingerprint($"{id}.flac", TimeSpan.FromMinutes(4), values), DateTime.UnixEpoch.AddSeconds(id));

    private sealed class FakeFingerprintCatalogRepository(IReadOnlyList<StoredFingerprint> items)
        : IFingerprintCatalogRepository
    {
        public Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(int algorithm, CancellationToken cancellationToken = default)
            => Task.FromResult(items);
    }

    private sealed class FakeSketchRepository : IFingerprintSegmentSketchRepository
    {
        private readonly Dictionary<long, DateTime> _states = [];
        private readonly Dictionary<long, IReadOnlyCollection<FingerprintSegmentSketch>> _sketches = [];

        public Task<IReadOnlyDictionary<long, DateTime>> GetTrackStatesAsync(int algorithm, CandidateGenerationOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<long, DateTime>>(_states);

        public Task<IReadOnlyList<FingerprintSegmentSketch>> GetAllAsync(int algorithm, CandidateGenerationOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FingerprintSegmentSketch>>(_sketches.Values.SelectMany(items => items).ToArray());

        public Task ReplaceTrackAsync(StoredFingerprint fingerprint, CandidateGenerationOptions options, IReadOnlyCollection<FingerprintSegmentSketch> sketches, CancellationToken cancellationToken = default)
        {
            _states[fingerprint.TrackId] = fingerprint.ExtractedAtUtc;
            _sketches[fingerprint.TrackId] = sketches.ToArray();
            return Task.CompletedTask;
        }

        public Task PruneAsync(int algorithm, CandidateGenerationOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateReview>>([]);

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(excluded);
    }
}
