using TrackMatch.Application;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Candidate相対品質解析Serviceの依存解析待ち・キャッシュ再利用・強制再解析を検証する。
/// </summary>
public sealed class CandidateQualityAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_ReturnsNullUntilBothTrackAnalysesAreReady()
    {
        var trackRepository = new TrackRepository();
        trackRepository.Values[1] = CreateTrackAnalysis(1);
        var candidateRepository = new CandidateRepository();
        var analyzer = new RecordingAnalyzer();
        var service = new CandidateQualityAnalysisService(analyzer, trackRepository, candidateRepository);

        var result = await service.AnalyzeAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, analyzer.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_ReusesCurrentSuccessfulCandidateCache()
    {
        var trackRepository = CreateReadyTrackRepository();
        var candidateRepository = new CandidateRepository();
        candidateRepository.Value = CreateCandidateResult();
        var analyzer = new RecordingAnalyzer();
        var service = new CandidateQualityAnalysisService(analyzer, trackRepository, candidateRepository);

        var result = await service.AnalyzeAsync(CreateRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(candidateRepository.Value, result);
        Assert.Equal(0, analyzer.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_ForceReanalyzesAndPersistsResult()
    {
        var trackRepository = CreateReadyTrackRepository();
        var candidateRepository = new CandidateRepository { Value = CreateCandidateResult() };
        var analyzer = new RecordingAnalyzer();
        var service = new CandidateQualityAnalysisService(analyzer, trackRepository, candidateRepository);

        var result = await service.AnalyzeAsync(
            CreateRequest(),
            force: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, analyzer.CallCount);
        Assert.Equal(QualityAnalysisStatus.Analyzed, candidateRepository.Value!.Status);
    }

    [Fact]
    public async Task AnalyzeAsync_CancellationRemovesOnlyOwnedAnalyzingGeneration()
    {
        var trackRepository = CreateReadyTrackRepository();
        var candidateRepository = new CandidateRepository();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new BlockingAnalyzer(started);
        var service = new CandidateQualityAnalysisService(analyzer, trackRepository, candidateRepository);
        using var cancellation = new CancellationTokenSource();
        var run = service.AnalyzeAsync(CreateRequest(), force: true, cancellationToken: cancellation.Token);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var oldMarker = Assert.IsType<DateTime>(candidateRepository.Value!.ComparedAtUtc);
        Assert.Equal(QualityAnalysisStatus.Analyzing, candidateRepository.Value.Status);

        // 新しいセッションが同PairへAnalyzingを書いた状態を再現する。
        // 古いセッションのCancelは開始時刻が異なる新状態を削除してはならない。
        var newerMarker = oldMarker.AddTicks(1);
        candidateRepository.Value = CreateCandidateState(QualityAnalysisStatus.Analyzing, newerMarker);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.NotNull(candidateRepository.Value);
        Assert.Equal(QualityAnalysisStatus.Analyzing, candidateRepository.Value!.Status);
        Assert.Equal(newerMarker, candidateRepository.Value.ComparedAtUtc);
    }

    private static CandidateQualityAnalysisRequest CreateRequest()
        => new(
            new CandidateComparison(
                1,
                2,
                0.99,
                0,
                TimeSpan.Zero,
                100,
                TimeSpan.FromSeconds(30),
                1,
                1,
                1),
            Path.Combine(Path.GetTempPath(), "a.flac"),
            Path.Combine(Path.GetTempPath(), "b.flac"));

    private static TrackRepository CreateReadyTrackRepository()
    {
        var repository = new TrackRepository();
        repository.Values[1] = CreateTrackAnalysis(1);
        repository.Values[2] = CreateTrackAnalysis(2);
        return repository;
    }

    private static TrackQualityAnalysis CreateTrackAnalysis(long trackId)
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
            0,
            18000,
            false,
            null,
            0.02,
            null,
            DateTime.UnixEpoch,
            null);

    private static CandidateQualityComparison CreateCandidateResult()
        => new(
            1,
            2,
            QualityAnalysisVersions.CandidateQualityComparison,
            QualityAnalysisStatus.Analyzed,
            3,
            3,
            0.1,
            0,
            0,
            true,
            0,
            DateTime.UnixEpoch,
            null);

    private static CandidateQualityComparison CreateCandidateState(QualityAnalysisStatus status, DateTime comparedAtUtc)
        => new(
            1,
            2,
            QualityAnalysisVersions.CandidateQualityComparison,
            status,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            comparedAtUtc,
            null);

    private sealed class RecordingAnalyzer : ICandidateQualityAnalyzer
    {
        public int CallCount { get; private set; }

        public Task<CandidateQualityComparison> AnalyzeAsync(
            CandidateComparison candidate,
            string pathA,
            string pathB,
            TrackQualityAnalysis analysisA,
            TrackQualityAnalysis analysisB,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CreateCandidateResult());
        }
    }

    private sealed class BlockingAnalyzer(TaskCompletionSource<bool> started) : ICandidateQualityAnalyzer
    {
        public async Task<CandidateQualityComparison> AnalyzeAsync(
            CandidateComparison candidate,
            string pathA,
            string pathB,
            TrackQualityAnalysis analysisA,
            TrackQualityAnalysis analysisB,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateCandidateResult();
        }
    }

    private sealed class TrackRepository : ITrackQualityAnalysisRepository
    {
        public Dictionary<long, TrackQualityAnalysis> Values { get; } = [];

        public Task<TrackQualityAnalysis?> GetAsync(long trackId, CancellationToken cancellationToken = default)
        {
            Values.TryGetValue(trackId, out var value);
            return Task.FromResult(value);
        }

        public Task UpsertAsync(TrackQualityAnalysis analysis, CancellationToken cancellationToken = default)
        {
            Values[analysis.TrackId] = analysis;
            return Task.CompletedTask;
        }

        public Task<bool> TryCompleteAnalyzingAsync(
            TrackQualityAnalysis analysis,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (!Values.TryGetValue(analysis.TrackId, out var current)
                || current.Status != QualityAnalysisStatus.Analyzing
                || current.AnalyzedAtUtc != analyzingStartedAtUtc)
            {
                return Task.FromResult(false);
            }

            Values[analysis.TrackId] = analysis;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAnalyzingAsync(
            long trackId,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (!Values.TryGetValue(trackId, out var current)
                || current.Status != QualityAnalysisStatus.Analyzing
                || current.AnalyzedAtUtc != analyzingStartedAtUtc)
            {
                return Task.FromResult(false);
            }

            Values.Remove(trackId);
            return Task.FromResult(true);
        }

        public Task DeleteAsync(long trackId, CancellationToken cancellationToken = default)
        {
            Values.Remove(trackId);
            return Task.CompletedTask;
        }
    }

    private sealed class CandidateRepository : ICandidateQualityComparisonRepository
    {
        public CandidateQualityComparison? Value { get; set; }

        public Task<CandidateQualityComparison?> GetAsync(
            long trackIdA,
            long trackIdB,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Value);

        public Task UpsertAsync(
            CandidateQualityComparison comparison,
            CancellationToken cancellationToken = default)
        {
            Value = comparison;
            return Task.CompletedTask;
        }

        public Task<bool> TryCompleteAnalyzingAsync(
            CandidateQualityComparison comparison,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (Value is null
                || Value.Status != QualityAnalysisStatus.Analyzing
                || Value.ComparedAtUtc != analyzingStartedAtUtc)
            {
                return Task.FromResult(false);
            }

            Value = comparison;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAnalyzingAsync(
            long trackIdA,
            long trackIdB,
            DateTime analyzingStartedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (Value is null
                || Value.Status != QualityAnalysisStatus.Analyzing
                || Value.ComparedAtUtc != analyzingStartedAtUtc)
            {
                return Task.FromResult(false);
            }

            Value = null;
            return Task.FromResult(true);
        }

        public Task DeleteAsync(long trackIdA, long trackIdB, CancellationToken cancellationToken = default)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }
}
