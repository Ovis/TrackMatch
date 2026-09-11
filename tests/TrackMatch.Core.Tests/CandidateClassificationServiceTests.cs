using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using TrackMatch.Core.Persistence;
using Xunit;

namespace TrackMatch.Core.Tests;

public sealed class CandidateClassificationServiceTests
{
    [Fact]
    public async Task ClassifyAsync_ClassifiesComparisonsAndPersistsProfile()
    {
        var comparisons = new FakeComparisonRepository(
        [
            new CandidateComparison(1, 2, 0.99, 0, TimeSpan.Zero, 100, TimeSpan.FromSeconds(10), 0.98, 0.97, 0.99),
        ]);
        var classifications = new FakeClassificationRepository();
        var service = new CandidateClassificationService(comparisons, classifications);
        var profile = new RelationshipThresholdProfile(
            0.95, 0.95, 0.95,
            0.90, 0.95, 0.60, 0.60,
            0.80);

        await service.ClassifyAsync(profile, TestContext.Current.CancellationToken);

        var saved = Assert.Single(classifications.Saved);
        Assert.Equal(AudioRelationshipKind.DuplicateCandidate, saved.Kind);
        Assert.Contains("DuplicateMinimumSimilarity", saved.ThresholdProfileJson, StringComparison.Ordinal);
    }

    private sealed class FakeComparisonRepository(IReadOnlyList<CandidateComparison> values) : ICandidateComparisonRepository
    {
        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyCollection<CandidateComparison> comparisons, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CandidateComparison>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(values);

        public Task<IReadOnlyDictionary<CandidatePairKey, DateTime>> GetComparedAtUtcAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<CandidatePairKey, DateTime>>(new Dictionary<CandidatePairKey, DateTime>());
    }

    private sealed class FakeClassificationRepository : ICandidateClassificationRepository
    {
        public IReadOnlyCollection<CandidateClassification> Saved { get; private set; } = [];

        public Task ReplaceAllAsync(IReadOnlyCollection<CandidateClassification> classifications, CancellationToken cancellationToken = default)
        {
            Saved = classifications;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CandidateClassificationReportRow>> GetReportAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandidateClassificationReportRow>>([]);
    }
}
