using System.Collections.Concurrent;
using TrackMatch.Application;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// Track品質解析Coordinatorのキャッシュ再利用・優先度制御・進捗集計を検証する。
/// </summary>
public sealed class TrackQualityAnalysisCoordinatorTests
{
    [Fact]
    public async Task RunAsync_ReusesCurrentSuccessfulCache()
    {
        var repository = new InMemoryRepository();
        repository.Values[1] = CreateResult(1, QualityAnalysisStatus.Analyzed);
        var analyzer = new RecordingAnalyzer();
        var coordinator = new TrackQualityAnalysisCoordinator(analyzer, repository, workerCount: 1);
        var progressValues = new List<TrackQualityAnalysisProgress>();
        var progress = new InlineProgress<TrackQualityAnalysisProgress>(progressValues.Add);

        await coordinator.RunAsync(
            [
                new TrackQualityAnalysisRequest(1, Path.Combine(Path.GetTempPath(), "a.flac")),
                new TrackQualityAnalysisRequest(2, Path.Combine(Path.GetTempPath(), "b.flac")),
            ],
            progress,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(1L, analyzer.StartedTrackIds);
        Assert.Contains(2L, analyzer.StartedTrackIds);
        Assert.NotEmpty(progressValues);
        var final = progressValues[^1];
        Assert.Equal(2, final.CompletedCount);
        Assert.Equal(2, final.TotalCount);
        Assert.Equal(0, final.FailedCount);
    }

    [Fact]
    public async Task Prioritize_MovesSelectedPendingTrackAheadOfNormalQueue()
    {
        var repository = new InMemoryRepository();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new RecordingAnalyzer(async trackId =>
        {
            if (trackId == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task;
            }
        });
        var coordinator = new TrackQualityAnalysisCoordinator(analyzer, repository, workerCount: 1);
        var run = coordinator.RunAsync(
            CreateRequests(1, 2, 3),
            cancellationToken: TestContext.Current.CancellationToken);

        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        coordinator.Prioritize(3);
        releaseFirst.TrySetResult(true);
        await run;

        Assert.Equal(new long[] { 1, 3, 2 }, analyzer.StartedTrackIds);
    }

    [Fact]
    public async Task RunAsync_ContinuesAfterSingleTrackFailureAndReportsFailure()
    {
        var repository = new InMemoryRepository();
        var analyzer = new RecordingAnalyzer(trackId =>
            trackId == 2
                ? throw new InvalidOperationException("test failure")
                : Task.CompletedTask);
        var coordinator = new TrackQualityAnalysisCoordinator(analyzer, repository, workerCount: 2);
        TrackQualityAnalysisProgress? latest = null;
        var progress = new InlineProgress<TrackQualityAnalysisProgress>(value => latest = value);

        await coordinator.RunAsync(
            CreateRequests(1, 2, 3),
            progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, analyzer.StartedTrackIds.Count);
        Assert.NotNull(latest);
        Assert.Equal(3, latest!.CompletedCount);
        Assert.Equal(1, latest.FailedCount);
        Assert.Equal(QualityAnalysisStatus.Failed, repository.Values[2].Status);
    }

    private static IReadOnlyCollection<TrackQualityAnalysisRequest> CreateRequests(params long[] trackIds)
        => trackIds
            .Select(trackId => new TrackQualityAnalysisRequest(
                trackId,
                Path.Combine(Path.GetTempPath(), $"track-{trackId}.flac")))
            .ToArray();

    private static TrackQualityAnalysis CreateResult(long trackId, QualityAnalysisStatus status)
        => new(
            trackId,
            QualityAnalysisVersions.TrackQualityAnalysis,
            status,
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
            0.01,
            null,
            DateTime.UtcNow,
            status == QualityAnalysisStatus.Failed ? "failed" : null);

    private sealed class RecordingAnalyzer(Func<long, Task>? onAnalyze = null) : ITrackQualityAnalyzer
    {
        private readonly ConcurrentQueue<long> _startedTrackIds = new();

        public IReadOnlyList<long> StartedTrackIds => _startedTrackIds.ToArray();

        public async Task<TrackQualityAnalysis> AnalyzeAsync(
            long trackId,
            string path,
            CancellationToken cancellationToken = default)
        {
            _startedTrackIds.Enqueue(trackId);
            if (onAnalyze is not null)
            {
                await onAnalyze(trackId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(trackId, QualityAnalysisStatus.Analyzed);
        }
    }

    private sealed class InMemoryRepository : ITrackQualityAnalysisRepository
    {
        public ConcurrentDictionary<long, TrackQualityAnalysis> Values { get; } = new();

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

        public Task DeleteAsync(long trackId, CancellationToken cancellationToken = default)
        {
            Values.TryRemove(trackId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
