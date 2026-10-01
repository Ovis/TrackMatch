using TrackMatch.App.Quality;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Review差分CandidateのQuality CacheをN+1なしで復元することを検証する。
/// </summary>
public sealed class CandidateQualityHydratorTests
{
    [Fact]
    public async Task HydrateAsync_UsesOneBatchPerRepositoryAndReturnsOnlyMissingPairs()
    {
        var complete = new CandidateReviewItemViewModel(CreateRow(1, 2));
        var missingComparison = new CandidateReviewItemViewModel(CreateRow(2, 3));
        var trackRepository = new RecordingTrackRepository(
            new Dictionary<long, TrackQualityAnalysis>
            {
                [1] = CreateTrack(1),
                [2] = CreateTrack(2),
                [3] = CreateTrack(3),
            });
        var candidateRepository = new RecordingCandidateRepository(
            new Dictionary<CandidatePairKey, CandidateQualityComparison>
            {
                [CandidatePairKey.Create(1, 2)] = CreateComparison(1, 2),
            });

        var missing = await CandidateQualityHydrator.HydrateAsync(
            [complete, missingComparison],
            trackRepository,
            candidateRepository,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, trackRepository.BatchCallCount);
        Assert.Equal([1L, 2L, 3L], trackRepository.RequestedTrackIds.Order());
        Assert.Equal(1, candidateRepository.BatchCallCount);
        Assert.Equal(2, candidateRepository.RequestedPairs.Count);
        Assert.Same(missingComparison, Assert.Single(missing));
        Assert.True(complete.CanReanalyzeQuality);
        Assert.Equal("音質解析: 完了", complete.QualityStatusText);
        Assert.Equal("音質解析: 解析待ち", missingComparison.QualityStatusText);
    }

    [Fact]
    public async Task HydrateAsync_EmptyItems_DoesNotAccessRepositories()
    {
        var trackRepository = new RecordingTrackRepository(new Dictionary<long, TrackQualityAnalysis>());
        var candidateRepository = new RecordingCandidateRepository(
            new Dictionary<CandidatePairKey, CandidateQualityComparison>());

        var missing = await CandidateQualityHydrator.HydrateAsync(
            [],
            trackRepository,
            candidateRepository,
            TestContext.Current.CancellationToken);

        Assert.Empty(missing);
        Assert.Equal(0, trackRepository.BatchCallCount);
        Assert.Equal(0, candidateRepository.BatchCallCount);
    }

    [Fact]
    public async Task HydrateAsync_ItemIsNoLongerCurrent_DoesNotApplyFetchedQuality()
    {
        var stale = new CandidateReviewItemViewModel(CreateRow(1, 2));
        var trackRepository = new RecordingTrackRepository(
            new Dictionary<long, TrackQualityAnalysis>
            {
                [1] = CreateTrack(1),
                [2] = CreateTrack(2),
            });
        var candidateRepository = new RecordingCandidateRepository(
            new Dictionary<CandidatePairKey, CandidateQualityComparison>
            {
                [CandidatePairKey.Create(1, 2)] = CreateComparison(1, 2),
            });

        var missing = await CandidateQualityHydrator.HydrateAsync(
            [stale],
            trackRepository,
            candidateRepository,
            TestContext.Current.CancellationToken,
            _ => false);

        Assert.Empty(missing);
        Assert.Equal("音質解析: 解析待ち", stale.QualityStatusText);
    }

    private static CandidateReviewReportRow CreateRow(long trackIdA, long trackIdB)
        => new(
            trackIdA,
            trackIdB,
            null,
            null,
            0.99,
            1,
            1,
            1,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(3),
            $"{trackIdA}.flac",
            $"{trackIdB}.flac",
            [],
            [],
            null,
            null,
            null,
            null,
            [],
            [],
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(3),
            100,
            100,
            "FLAC",
            "FLAC",
            "FLAC",
            "FLAC",
            900,
            900,
            44100,
            44100,
            16,
            16,
            2,
            2);

    private static TrackQualityAnalysis CreateTrack(long trackId)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            QualityAnalysisStatus.Analyzed,
            -12,
            -1,
            5,
            11,
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0.2,
            19000,
            false,
            null,
            0.02,
            0.9,
            DateTime.UnixEpoch,
            null);

    private static CandidateQualityComparison CreateComparison(long trackIdA, long trackIdB)
        => new(
            trackIdA,
            trackIdB,
            QualityAnalysisVersions.CandidateQualityComparison,
            QualityAnalysisStatus.Analyzed,
            0.2,
            0.2,
            0.1,
            0.1,
            0.1,
            true,
            0,
            DateTime.UnixEpoch,
            null);

    private sealed class RecordingTrackRepository(
        IReadOnlyDictionary<long, TrackQualityAnalysis> values) : ITrackQualityAnalysisRepository
    {
        public int BatchCallCount { get; private set; }
        public IReadOnlyCollection<long> RequestedTrackIds { get; private set; } = [];

        public Task<IReadOnlyDictionary<long, TrackQualityAnalysis>> GetByTrackIdsAsync(
            IReadOnlyCollection<long> trackIds,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            RequestedTrackIds = trackIds.ToArray();
            return Task.FromResult<IReadOnlyDictionary<long, TrackQualityAnalysis>>(
                values.Where(pair => trackIds.Contains(pair.Key)).ToDictionary());
        }

        public Task<TrackQualityAnalysis?> GetAsync(long trackId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("単件取得は呼び出されない想定です。");

        public Task UpsertAsync(TrackQualityAnalysis analysis, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TryCompleteAnalyzingAsync(
            TrackQualityAnalysis analysis,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAnalyzingAsync(
            long trackId,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(long trackId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingCandidateRepository(
        IReadOnlyDictionary<CandidatePairKey, CandidateQualityComparison> values) : ICandidateQualityComparisonRepository
    {
        public int BatchCallCount { get; private set; }
        public IReadOnlyCollection<CandidatePairKey> RequestedPairs { get; private set; } = [];

        public Task<IReadOnlyDictionary<CandidatePairKey, CandidateQualityComparison>> GetByPairsAsync(
            IReadOnlyCollection<CandidatePairKey> pairKeys,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            RequestedPairs = pairKeys.ToArray();
            return Task.FromResult<IReadOnlyDictionary<CandidatePairKey, CandidateQualityComparison>>(
                values.Where(pair => pairKeys.Contains(pair.Key)).ToDictionary());
        }

        public Task<CandidateQualityComparison?> GetAsync(
            long trackIdA,
            long trackIdB,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("単件取得は呼び出されない想定です。");

        public Task UpsertAsync(
            CandidateQualityComparison comparison,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TryCompleteAnalyzingAsync(
            CandidateQualityComparison comparison,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAnalyzingAsync(
            long trackIdA,
            long trackIdB,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(
            long trackIdA,
            long trackIdB,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
