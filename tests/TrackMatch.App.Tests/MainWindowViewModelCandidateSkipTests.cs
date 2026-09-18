using System.Reflection;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Playback;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// MainWindowのCandidate一覧でレビュー省略がレビュー作業件数やFilterへ混入しないことを検証する。
/// </summary>
public sealed class MainWindowViewModelCandidateSkipTests
{
    [Fact]
    public void CandidateFilters_SkippedPairAppearsOnlyInAllAndIsExcludedFromReviewCounts()
    {
        using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
        var rows = new[]
        {
            CreateRow(null, 1, 2),
            CreateRow(null, 1, 3),
            CreateRow(null, 2, 3),
            CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 4, 5),
        };
        var group = new DuplicateGroup(10, 1, 1, DuplicateGroupKeepStatus.Selected, [1, 2, 3], [1, 2, 3]);
        ReplaceAllCandidates(viewModel, CreateItems(rows, [group]));

        viewModel.CandidateListMode = CandidateReviewListMode.All;

        Assert.Equal(4, viewModel.TotalCandidateCount);
        Assert.Equal(0, viewModel.UnreviewedCount);
        Assert.Equal(1, viewModel.ReviewedCount);
        Assert.Equal(4, viewModel.Candidates.Count);
        Assert.Contains(viewModel.Candidates, item => item.TrackIdA == 2 && item.TrackIdB == 3 && item.IsReviewSkipped);

        viewModel.CandidateListMode = CandidateReviewListMode.Unreviewed;

        Assert.Empty(viewModel.Candidates);
        Assert.DoesNotContain(viewModel.Candidates, item => item.IsReviewSkipped);

        viewModel.CandidateListMode = CandidateReviewListMode.Reviewed;
        Assert.Single(viewModel.Candidates);
        Assert.All(viewModel.Candidates, item => Assert.True(item.IsReviewed));

        viewModel.CandidateListMode = CandidateReviewListMode.ReReviewRecommended;
        Assert.Empty(viewModel.Candidates);
    }

    [Fact]
    public void CandidateFilters_WhenKeepIsUnique_AllRemainingPairsStaySkipped()
    {
        using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
        var rows = new[]
        {
            CreateRow(null, 1, 2),
            CreateRow(null, 1, 3),
            CreateRow(null, 2, 3),
        };

        ReplaceAllCandidates(
            viewModel,
            CreateItems(rows, [new DuplicateGroup(10, 1, 1, DuplicateGroupKeepStatus.Selected, [1, 2, 3], [1, 2, 3])]));
        viewModel.CandidateListMode = CandidateReviewListMode.All;
        Assert.True(viewModel.Candidates.Single(item => item.TrackIdA == 2 && item.TrackIdB == 3).IsReviewSkipped);

        // Keep変更後の一覧再読込と同じく、最新Projectionから派生状態を作り直す。
        ReplaceAllCandidates(
            viewModel,
            CreateItems(rows, [new DuplicateGroup(10, 1, 2, DuplicateGroupKeepStatus.Selected, [1, 2, 3], [1, 2, 3])]));
        viewModel.CandidateListMode = CandidateReviewListMode.Unreviewed;
        viewModel.CandidateListMode = CandidateReviewListMode.All;

        Assert.All(viewModel.Candidates, item => Assert.True(item.IsReviewSkipped));
    }

    private static IReadOnlyList<CandidateReviewItemViewModel> CreateItems(
        IReadOnlyList<CandidateReviewReportRow> rows,
        IReadOnlyList<DuplicateGroup> groups)
    {
        var states = CandidateReviewPresentationStateResolver.Resolve(rows, groups, []);
        return rows.Select(row => new CandidateReviewItemViewModel(
            row,
            states[CandidatePairKey.Create(row.TrackIdA, row.TrackIdB)])).ToArray();
    }

    private static void ReplaceAllCandidates(
        MainWindowViewModel viewModel,
        IReadOnlyList<CandidateReviewItemViewModel> items)
    {
        var field = typeof(MainWindowViewModel).GetField("_allCandidates", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Candidate一覧の内部フィールドを取得できません。");
        var list = (List<CandidateReviewItemViewModel>)field.GetValue(viewModel)!;
        list.Clear();
        list.AddRange(items);
    }

    private static CandidateReviewReportRow CreateRow(CandidateReviewDecision? decision, long trackIdA, long trackIdB)
        => new(
            trackIdA, trackIdB, null, null, 0.99, 1, 1, 1,
            TimeSpan.Zero, TimeSpan.FromMinutes(3),
            $"{trackIdA}.flac", $"{trackIdB}.flac", ["Artist"], ["Artist"],
            $"Track {trackIdA}", $"Track {trackIdB}", "Album", "Album", [], [],
            TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), 100, 100,
            "FLAC", "FLAC", "FLAC", "FLAC", 900, 900, 44100, 44100, 16, 16, 2, 2,
            decision);

    private sealed class FakeSynchronizedPlaybackService : ISynchronizedPlaybackService
    {
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public event Action<string>? PlaybackFailed { add { } remove { } }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public PlaybackOffsets Offsets { get; private set; } = new(TimeSpan.Zero, TimeSpan.Zero);
        public SynchronizedPlaybackMode Mode { get; set; } = SynchronizedPlaybackMode.StereoOverlay;
        public float VolumeA { get; set; } = 1f;
        public float VolumeB { get; set; } = 1f;
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }

        public void Load(string pathA, string pathB, TimeSpan bestOffset)
        {
            Position = TimeSpan.Zero;
            Duration = TimeSpan.FromMinutes(3);
            Offsets = PlaybackOffsets.Normalize(TimeSpan.Zero, bestOffset);
        }

        public void Play() { IsPlaying = true; IsPaused = false; }
        public void Pause() { IsPlaying = false; IsPaused = true; }
        public void Stop() { IsPlaying = false; IsPaused = false; Position = TimeSpan.Zero; }
        public void Seek(TimeSpan position) => Position = position;
        public void SetOffsets(TimeSpan offsetA, TimeSpan offsetB) => Offsets = PlaybackOffsets.Normalize(offsetA, offsetB);
        public void Dispose() { }
    }
}
