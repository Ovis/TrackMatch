using System.Reflection;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Playback;
using Xunit;

namespace TrackMatch.App.Tests;

/// <summary>
/// ジャンルFilterがPairではなく重複候補Group全体を維持して絞り込むことを検証する。
/// </summary>
public sealed class MainWindowViewModelGenreFilterTests
{
    [Fact]
    public void GenreFilter_MatchingOneTrackKeepsEveryPairInCandidateGroup()
    {
        using var viewModel = CreateViewModel();

        SelectGenre(viewModel, "J-POPS");

        Assert.Equal(3, viewModel.Candidates.Count);
        Assert.Contains(viewModel.Candidates, item => IsPair(item, 1, 2));
        Assert.Contains(viewModel.Candidates, item => IsPair(item, 1, 3));
        Assert.Contains(viewModel.Candidates, item => IsPair(item, 2, 3));
    }

    [Fact]
    public void GenreFilter_AndModeMatchesGenresAcrossDifferentTracksInSameGroup()
    {
        using var viewModel = CreateViewModel();

        SelectGenre(viewModel, "J-POPS");
        SelectGenre(viewModel, "POPS");
        viewModel.SelectedGenreFilterMode = GenreFilterMode.And;

        // J-POPSとPOPSは別Trackに付いていても、Group全体のGenre集合には双方が存在する。
        // SoundTrackのCも同じGroupの比較対象なので、音質判断から欠落させない。
        Assert.Equal(3, viewModel.Candidates.Count);
        Assert.Contains(viewModel.Candidates, item => IsPair(item, 1, 3));
        Assert.Contains(viewModel.Candidates, item => IsPair(item, 2, 3));
    }

    [Fact]
    public void GenreFilter_SoundTrackKeepsPairsThatDoNotDirectlyContainSoundTrackTrack()
    {
        using var viewModel = CreateViewModel();

        SelectGenre(viewModel, "SoundTrack");

        // A-B自体にはSoundTrackが無いが、Cを含む同一Group全体を表示する仕様。
        Assert.Equal(3, viewModel.Candidates.Count);
        Assert.Contains(viewModel.Candidates, item => IsPair(item, 1, 2));
    }

    [Fact]
    public void GenreOptions_MergeCaseVariantsAndUseMostFrequentSpelling()
    {
        using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
        ReplaceAllCandidates(
            viewModel,
            [
                CreateItem(1, 2, ["J-Pop"], ["J-POP"]),
                CreateItem(2, 3, ["J-POP"], ["j-pop"]),
            ]);
        RefreshGenreOptions(viewModel);
        viewModel.CandidateListMode = CandidateReviewListMode.All;

        var option = Assert.Single(viewModel.GenreOptions);
        Assert.Equal("J-POP", option.DisplayName);
    }

    [Fact]
    public void GenreOptions_ExposeMissingGenreAsSelectableValue()
    {
        using var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
        ReplaceAllCandidates(viewModel, [CreateItem(1, 2, [], ["Anime"])]);
        RefreshGenreOptions(viewModel);
        viewModel.CandidateListMode = CandidateReviewListMode.All;

        SelectGenre(viewModel, "ジャンル未設定");

        Assert.Single(viewModel.Candidates);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var viewModel = new MainWindowViewModel(new FakeSynchronizedPlaybackService());
        ReplaceAllCandidates(
            viewModel,
            [
                CreateItem(1, 2, ["J-POPS"], ["POPS"]),
                CreateItem(1, 3, ["J-POPS"], ["SoundTrack"]),
                CreateItem(2, 3, ["POPS"], ["SoundTrack"]),
                CreateItem(10, 11, ["Classical"], ["Classical"]),
            ]);
        RefreshGenreOptions(viewModel);
        viewModel.CandidateListMode = CandidateReviewListMode.All;
        return viewModel;
    }

    private static CandidateReviewItemViewModel CreateItem(
        long trackIdA,
        long trackIdB,
        IReadOnlyList<string> genresA,
        IReadOnlyList<string> genresB)
        => new(CreateRow(trackIdA, trackIdB, genresA, genresB));

    private static CandidateReviewReportRow CreateRow(
        long trackIdA,
        long trackIdB,
        IReadOnlyList<string> genresA,
        IReadOnlyList<string> genresB)
        => new(
            trackIdA, trackIdB, null, null, 0.99, 1, 1, 1,
            TimeSpan.Zero, TimeSpan.FromMinutes(3),
            $"{trackIdA}.flac", $"{trackIdB}.flac", ["Artist"], ["Artist"],
            $"Track {trackIdA}", $"Track {trackIdB}", "Album", "Album", genresA, genresB,
            TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), 100, 100,
            "FLAC", "FLAC", "FLAC", "FLAC", 900, 900, 44100, 44100, 16, 16, 2, 2,
            null);

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

    private static void RefreshGenreOptions(MainWindowViewModel viewModel)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "RefreshGenreOptions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ジャンル選択肢の更新Methodを取得できません。");
        method.Invoke(viewModel, null);
    }

    private static void SelectGenre(MainWindowViewModel viewModel, string displayName)
    {
        var option = Assert.Single(viewModel.GenreOptions.Where(item => item.DisplayName == displayName));
        option.IsSelected = true;
    }

    private static bool IsPair(CandidateReviewItemViewModel item, long left, long right)
        => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB) == CandidatePairKey.Create(left, right);

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

        public void Load(string pathA, string pathB, TimeSpan bestOffset, TimeSpan durationA, TimeSpan durationB)
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
