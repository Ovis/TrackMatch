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
            CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 1, 2, preferredTrackId: 1),
            CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 1, 3, preferredTrackId: 1),
            CreateRow(null, 2, 3),
            CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 4, 5, preferredTrackId: 4),
        };
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(1, 3, 1),
            Confirmed(4, 5, 4),
        };
        var groups = new[]
        {
            new DuplicateGroup(10, 1, 1, DuplicateGroupKeepStatus.Selected, [1, 2, 3], [1, 2, 3]),
            new DuplicateGroup(20, 1, 4, DuplicateGroupKeepStatus.Selected, [4, 5], [4, 5]),
        };
        ReplaceAllCandidates(viewModel, CreateItems(rows, groups, reviews));

        viewModel.CandidateListMode = CandidateReviewListMode.All;

        Assert.Equal(4, viewModel.TotalCandidateCount);
        Assert.Equal(0, viewModel.UnreviewedCount);
        Assert.Equal(3, viewModel.ReviewedCount);
        Assert.Equal(4, viewModel.Candidates.Count);
        Assert.True(viewModel.Candidates.Single(item => item.TrackIdA == 2 && item.TrackIdB == 3).IsReviewSkipped);

        viewModel.CandidateListMode = CandidateReviewListMode.Unreviewed;
        Assert.Empty(viewModel.Candidates);

        viewModel.CandidateListMode = CandidateReviewListMode.Reviewed;
        Assert.Equal(3, viewModel.Candidates.Count);
        Assert.All(viewModel.Candidates, item => Assert.True(item.IsReviewed));
    }

    [Fact]
    public void CandidateFilters_WhenKeepBecomesAmbiguous_RequiredTopPairReturnsToUnreviewed()
    {
        using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
        var rows = new[]
        {
            CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 1, 2, preferredTrackId: 1),
            CreateRow(null, 1, 3),
            CreateRow(CandidateReviewDecision.ConfirmedDuplicate, 2, 3, preferredTrackId: 3),
        };
        var reviews = new[]
        {
            Confirmed(1, 2, 1),
            Confirmed(2, 3, 3),
        };
        var group = new DuplicateGroup(10, 1, null, DuplicateGroupKeepStatus.Unselected, [1, 2, 3], [1, 2, 3]);
        ReplaceAllCandidates(viewModel, CreateItems(rows, [group], reviews));

        // 初期値もUnreviewedなので、一度Allを適用してからFilterを戻し、実際のFilter経路を通す。
        viewModel.CandidateListMode = CandidateReviewListMode.All;
        viewModel.CandidateListMode = CandidateReviewListMode.Unreviewed;

        // 1と3はいずれもTop候補なので、この比較を省略するとKeepを一意化できない。
        var required = Assert.Single(viewModel.Candidates);
        Assert.Equal(CandidatePairKey.Create(1, 3), CandidatePairKey.Create(required.TrackIdA, required.TrackIdB));
        Assert.False(required.IsReviewSkipped);
    }

    private static IReadOnlyList<CandidateReviewItemViewModel> CreateItems(
        IReadOnlyList<CandidateReviewReportRow> rows,
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyList<CandidateReview> reviews)
    {
        var states = CandidateReviewPresentationStateResolver.Resolve(rows, groups, reviews);
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

    private static CandidateReviewReportRow CreateRow(CandidateReviewDecision? decision, long trackIdA, long trackIdB, long? preferredTrackId = null)
        => new(
            trackIdA, trackIdB, null, null, 0.99, 1, 1, 1,
            TimeSpan.Zero, TimeSpan.FromMinutes(3),
            $"{trackIdA}.flac", $"{trackIdB}.flac", ["Artist"], ["Artist"],
            $"Track {trackIdA}", $"Track {trackIdB}", "Album", "Album", [], [],
            TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), 100, 100,
            "FLAC", "FLAC", "FLAC", "FLAC", 900, 900, 44100, 44100, 16, 16, 2, 2,
            decision,
            PreferredTrackId: preferredTrackId);

    private static CandidateReview Confirmed(long left, long right, long preferredTrackId)
        => new(CandidatePairKey.Create(left, right), CandidateReviewDecision.ConfirmedDuplicate, preferredTrackId, null);

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
