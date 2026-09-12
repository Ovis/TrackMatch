using TrackMatch.App;
using TrackMatch.App.Playback;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Playback;
using Xunit;

namespace TrackMatch.App.Tests;

public sealed class SynchronizedPlaybackControlsViewModelTests : IDisposable
{
    private readonly string _pathA = Path.GetTempFileName();
    private readonly string _pathB = Path.GetTempFileName();

    [Fact]
    public void LoadCandidate_AppliesBestOffsetAndResetsTemporaryState()
    {
        var service = new FakeSynchronizedPlaybackService
        {
            VolumeA = 0.25f,
            VolumeB = 0.5f,
        };
        using var viewModel = new SynchronizedPlaybackControlsViewModel(service);
        viewModel.VolumeAPercent = 25;
        viewModel.VolumeBPercent = 50;
        var candidate = CreateCandidate(TimeSpan.FromMilliseconds(-740));

        viewModel.LoadCandidate(candidate);

        Assert.Equal(_pathA, service.LoadedPathA);
        Assert.Equal(_pathB, service.LoadedPathB);
        Assert.Equal(TimeSpan.FromMilliseconds(-740), service.LoadedBestOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(740), service.Offsets.A);
        Assert.Equal(TimeSpan.Zero, service.Offsets.B);
        Assert.Equal(100d, viewModel.VolumeAPercent);
        Assert.Equal(100d, viewModel.VolumeBPercent);
        Assert.Equal(1f, service.VolumeA);
        Assert.Equal(1f, service.VolumeB);
        Assert.Equal(0d, viewModel.PositionSeconds);
    }

    [Fact]
    public void AdjustOffset_NormalizesBothDisplayedOffsets()
    {
        var service = new FakeSynchronizedPlaybackService();
        using var viewModel = new SynchronizedPlaybackControlsViewModel(service);
        viewModel.LoadCandidate(CreateCandidate(TimeSpan.FromMilliseconds(740)));

        viewModel.AdjustOffset(isTrackA: true, TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.Zero, service.Offsets.A);
        Assert.Equal(TimeSpan.FromMilliseconds(730), service.Offsets.B);
        Assert.Equal("0.000 s", viewModel.OffsetAText);
        Assert.Equal("0.730 s", viewModel.OffsetBText);
    }

    [Fact]
    public void ResetOffsetToAnalysis_DiscardsManualOffset()
    {
        var service = new FakeSynchronizedPlaybackService();
        using var viewModel = new SynchronizedPlaybackControlsViewModel(service);
        viewModel.LoadCandidate(CreateCandidate(TimeSpan.FromMilliseconds(740)));
        viewModel.AdjustOffset(isTrackA: false, TimeSpan.FromMilliseconds(50));

        viewModel.ResetOffsetToAnalysis();

        Assert.Equal(TimeSpan.Zero, service.Offsets.A);
        Assert.Equal(TimeSpan.FromMilliseconds(740), service.Offsets.B);
    }

    [Fact]
    public void Stop_ResetsCommonPositionToZero()
    {
        var service = new FakeSynchronizedPlaybackService();
        using var viewModel = new SynchronizedPlaybackControlsViewModel(service);
        viewModel.LoadCandidate(CreateCandidate(TimeSpan.Zero));
        viewModel.SeekSeconds(12.345);

        viewModel.Stop();

        Assert.Equal(TimeSpan.Zero, service.Position);
        Assert.Equal(0d, viewModel.PositionSeconds);
        Assert.Equal("00:00.000", viewModel.PositionText);
    }

    [Fact]
    public void ModeAndVolume_AreForwardedWithoutSeekOrOffsetChanges()
    {
        var service = new FakeSynchronizedPlaybackService();
        using var viewModel = new SynchronizedPlaybackControlsViewModel(service);
        viewModel.LoadCandidate(CreateCandidate(TimeSpan.FromMilliseconds(740)));
        viewModel.SeekSeconds(5);
        var position = service.Position;
        var offsets = service.Offsets;

        viewModel.SelectedMode = viewModel.Modes.Single(item => item.Value == SynchronizedPlaybackMode.SplitLeftRight);
        viewModel.VolumeAPercent = 70;
        viewModel.VolumeBPercent = 40;

        Assert.Equal(SynchronizedPlaybackMode.SplitLeftRight, service.Mode);
        Assert.Equal(0.7f, service.VolumeA, 3);
        Assert.Equal(0.4f, service.VolumeB, 3);
        Assert.Equal(position, service.Position);
        Assert.Equal(offsets, service.Offsets);
    }

    public void Dispose()
    {
        File.Delete(_pathA);
        File.Delete(_pathB);
    }

    private CandidateReviewItemViewModel CreateCandidate(TimeSpan bestOffset)
        => new(new CandidateReviewReportRow(
            1,
            2,
            null,
            null,
            0.95,
            0.9,
            0.9,
            1,
            bestOffset,
            TimeSpan.FromSeconds(120),
            _pathA,
            _pathB,
            [],
            [],
            "A",
            "B",
            null,
            null,
            [],
            [],
            TimeSpan.FromSeconds(180),
            TimeSpan.FromSeconds(181),
            1_000_000,
            900_000,
            "FLAC",
            "MP3",
            "FLAC",
            "MPEG Layer III",
            900,
            320,
            44100,
            48000,
            16,
            null,
            2,
            2));

    private sealed class FakeSynchronizedPlaybackService : ISynchronizedPlaybackService
    {
        public event EventHandler? PlaybackEnded
        {
            add { }
            remove { }
        }

        public event Action<string>? PlaybackFailed
        {
            add { }
            remove { }
        }

        public string? LoadedPathA { get; private set; }
        public string? LoadedPathB { get; private set; }
        public TimeSpan LoadedBestOffset { get; private set; }
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
            LoadedPathA = pathA;
            LoadedPathB = pathB;
            LoadedBestOffset = bestOffset;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.FromSeconds(181) + PlaybackOffsets.Normalize(TimeSpan.Zero, bestOffset).B;
            Offsets = PlaybackOffsets.Normalize(TimeSpan.Zero, bestOffset);
            VolumeA = 1f;
            VolumeB = 1f;
            IsPlaying = false;
            IsPaused = false;
        }

        public void Play()
        {
            IsPlaying = true;
            IsPaused = false;
        }

        public void Pause()
        {
            IsPlaying = false;
            IsPaused = true;
        }

        public void Stop()
        {
            IsPlaying = false;
            IsPaused = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan position)
            => Position = position <= TimeSpan.Zero
                ? TimeSpan.Zero
                : position >= Duration ? Duration : position;

        public void SetOffsets(TimeSpan offsetA, TimeSpan offsetB)
            => Offsets = PlaybackOffsets.Normalize(offsetA, offsetB);

        public void Dispose()
        {
        }
    }
}
