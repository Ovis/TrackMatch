using TrackMatch.Core.Candidates;
using TrackMatch.Core.Fingerprinting;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class CandidateGenerationServiceTests
{
    [Fact]
    public async Task GenerateAsync_SecondRunReindexesOnlyUpdatedTrackAndPreservesOtherPairs()
    {
        var extractedAt = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var values = Enumerable.Repeat(0x12345678u, 300).ToArray();
        var catalog = new MutableFingerprintCatalog(
        [
            Stored(1, values, extractedAt),
            Stored(2, values, extractedAt),
            Stored(3, values, extractedAt),
        ]);
        var sketches = new FakeSketchRepository();
        var pairs = new FakePairRepository();
        var sketcher = new FingerprintSegmentSketcher();
        var service = new CandidateGenerationService(
            catalog,
            sketches,
            pairs,
            new EmptyReviewRepository(),
            sketcher,
            new CandidatePairGenerator(sketcher));
        var options = new CandidateGenerationOptions();

        var initial = await service.GenerateAsync(2, options, TestContext.Current.CancellationToken);

        Assert.True(initial.IsFullRebuild);
        Assert.Equal(3, initial.IndexedTrackCount);
        Assert.Equal(3, pairs.Pairs.Count);

        var unchanged = await service.GenerateAsync(2, options, TestContext.Current.CancellationToken);

        Assert.False(unchanged.IsFullRebuild);
        Assert.Equal(0, unchanged.IndexedTrackCount);
        Assert.Empty(unchanged.Pairs);
        Assert.Equal(3, pairs.Pairs.Count);

        catalog.Items =
        [
            Stored(1, values, extractedAt.AddMinutes(1)),
            Stored(2, values, extractedAt),
            Stored(3, values, extractedAt),
        ];

        var updated = await service.GenerateAsync(2, options, TestContext.Current.CancellationToken);

        Assert.False(updated.IsFullRebuild);
        Assert.Equal(1, updated.IndexedTrackCount);
        Assert.Equal(2, updated.Pairs.Count);
        Assert.Equal(3, pairs.Pairs.Count);
        Assert.Contains(pairs.Pairs, pair => pair.TrackIdA == 2 && pair.TrackIdB == 3);
    }

    private static StoredFingerprint Stored(long id, IReadOnlyList<uint> values, DateTime extractedAt)
        => new(id, 2, new AudioFingerprint($"{id}.flac", TimeSpan.FromMinutes(4), values), extractedAt);

    private sealed class MutableFingerprintCatalog(IReadOnlyList<StoredFingerprint> items) : IFingerprintCatalogRepository
    {
        public IReadOnlyList<StoredFingerprint> Items { get; set; } = items;

        public Task<IReadOnlyList<StoredFingerprint>> GetActiveAsync(int algorithm, CancellationToken cancellationToken = default)
            => Task.FromResult(Items);
    }

    private sealed class FakeSketchRepository : IFingerprintSegmentSketchRepository
    {
        private readonly Dictionary<long, DateTime> _states = [];
        private readonly Dictionary<long, IReadOnlyList<FingerprintSegmentSketch>> _items = [];

        public Task<IReadOnlyDictionary<long, DateTime>> GetTrackStatesAsync(int algorithm, CandidateGenerationOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<long, DateTime>>(_states.ToDictionary());

        public Task<IReadOnlyList<FingerprintSegmentSketch>> GetAllAsync(int algorithm, CandidateGenerationOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FingerprintSegmentSketch>>(_items.Values.SelectMany(item => item).ToArray());

        public Task ReplaceTrackAsync(StoredFingerprint fingerprint, CandidateGenerationOptions options, IReadOnlyCollection<FingerprintSegmentSketch> sketches, CancellationToken cancellationToken = default)
        {
            _states[fingerprint.TrackId] = fingerprint.ExtractedAtUtc;
            _items[fingerprint.TrackId] = sketches.ToArray();
            return Task.CompletedTask;
        }

        public Task PruneAsync(int algorithm, CandidateGenerationOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakePairRepository : ICandidatePairRepository
    {
        public IReadOnlyList<CandidatePair> Pairs { get; private set; } = [];

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidatePair> pairs, CancellationToken cancellationToken = default)
        {
            Pairs = pairs.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidatePair>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Pairs);
    }

    private sealed class EmptyReviewRepository : ICandidateReviewRepository
    {
        public Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateReview>>([]);

        public Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<CandidatePairKey>>(new HashSet<CandidatePairKey>());
    }
}
